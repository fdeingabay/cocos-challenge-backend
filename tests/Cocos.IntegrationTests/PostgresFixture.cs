using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Cocos.IntegrationTests;

[CollectionDefinition(DatabaseCollection.Name)]
public sealed class DatabaseCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}

/// <summary>
/// Provee un PostgreSQL real para toda la suite, por uno de dos caminos:
///
///   1. Si esta definida la variable de entorno COCOS_TEST_DB, se usa ese servidor.
///      Sirve para correr sin Docker: una instalacion nativa, una base remota, o el
///      service container de un pipeline de CI.
///   2. Si no, se levanta uno con TestContainers.
///
/// En ambos casos el resto del flujo es identico: se siembra una base plantilla y cada
/// clase de test crea la suya con CREATE DATABASE ... TEMPLATE. Aislamiento total sin
/// pagar el arranque de un servidor por clase.
///
/// Deliberadamente NO se usa el provider in-memory de EF: no implementa locking de filas,
/// que es justamente el mecanismo que estos tests verifican. Un test de concurrencia contra
/// in-memory pasa siempre y no demuestra nada.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    /// <summary>Servidor Postgres ya disponible. Del connection string solo se toman host y credenciales.</summary>
    public const string ExternalServerVariable = "COCOS_TEST_DB";

    private const string TemplateDatabase = "cocos_template";
    private const string MaintenanceDatabase = "postgres";

    private readonly ConcurrentBag<string> _createdDatabases = [];

    private PostgreSqlContainer? _container;
    private string _serverConnectionString = null!;

    private bool UsesExternalServer => _container is null;

    public async Task InitializeAsync()
    {
        var external = Environment.GetEnvironmentVariable(ExternalServerVariable);

        if (string.IsNullOrWhiteSpace(external))
        {
            _container = new PostgreSqlBuilder("postgres:16-alpine")
                .WithDatabase(MaintenanceDatabase)
                .WithUsername("cocos")
                .WithPassword("cocos")
                .Build();

            await _container.StartAsync();
            _serverConnectionString = _container.GetConnectionString();
        }
        else
        {
            _serverConnectionString = external;
            await EnsureServerIsReachableAsync();
        }

        await CreateTemplateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            // Al destruir el contenedor se va todo: no hace falta limpiar base por base.
            await _container.DisposeAsync();
            return;
        }

        // Contra un servidor prestado si hay que devolverlo como estaba.
        foreach (var database in _createdDatabases)
            await TryDropDatabaseAsync(database);

        await TryDropDatabaseAsync(TemplateDatabase);
    }

    /// <summary>Base limpia, copiada de la plantilla ya sembrada.</summary>
    public async Task<string> CreateDatabaseAsync()
    {
        var name = $"cocos_test_{Guid.NewGuid():N}"[..24];

        await ExecuteOnMaintenanceAsync($"""CREATE DATABASE "{name}" TEMPLATE "{TemplateDatabase}";""");
        _createdDatabases.Add(name);

        return ConnectionStringFor(name);
    }

    private async Task EnsureServerIsReachableAsync()
    {
        try
        {
            await using var connection = new NpgsqlConnection(AdminConnectionStringFor(MaintenanceDatabase));
            await connection.OpenAsync();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"No se pudo conectar al servidor indicado en {ExternalServerVariable}. " +
                "Se espera un PostgreSQL 13 o superior, con permisos para crear bases de datos. " +
                $"Quitar la variable para volver a levantar uno con TestContainers. Detalle: {ex.Message}", ex);
        }
    }

    private async Task CreateTemplateAsync()
    {
        // WITH (FORCE) corta conexiones remanentes: si no, una corrida anterior interrumpida
        // deja la plantilla tomada y el CREATE ... TEMPLATE falla sin explicar por que.
        await ExecuteOnMaintenanceAsync($"""DROP DATABASE IF EXISTS "{TemplateDatabase}" WITH (FORCE);""");
        await ExecuteOnMaintenanceAsync($"""CREATE DATABASE "{TemplateDatabase}";""");

        var scriptsRoot = Path.Combine(SolutionRoot(), "db");

        // El orden importa: primero el esquema provisto por el challenge, despues los cambios.
        foreach (var script in new[] { "01-database.sql", "02-V2__challenge.sql" })
        {
            var sql = await File.ReadAllTextAsync(Path.Combine(scriptsRoot, script));

            await using var connection = new NpgsqlConnection(AdminConnectionStringFor(TemplateDatabase));
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private async Task ExecuteOnMaintenanceAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(AdminConnectionStringFor(MaintenanceDatabase));
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task TryDropDatabaseAsync(string database)
    {
        try
        {
            await ExecuteOnMaintenanceAsync($"""DROP DATABASE IF EXISTS "{database}" WITH (FORCE);""");
        }
        catch
        {
            // La limpieza es best-effort: no puede hacer fallar una corrida que ya termino.
        }
    }

    /// <summary>Connection string para la API bajo prueba, con pooling normal.</summary>
    private string ConnectionStringFor(string database)
        => new NpgsqlConnectionStringBuilder(_serverConnectionString) { Database = database }.ConnectionString;

    /// <summary>
    /// Connection string para las operaciones de administracion, SIN pooling: una conexión
    /// ociosa retenida por el pool bloquea tanto el DROP como el CREATE ... TEMPLATE.
    /// </summary>
    private string AdminConnectionStringFor(string database)
        => new NpgsqlConnectionStringBuilder(_serverConnectionString)
        {
            Database = database,
            Pooling = false
        }.ConnectionString;

    private static string SolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !directory.EnumerateFiles("Cocos.sln*").Any())
            directory = directory.Parent;

        return directory?.FullName ?? throw new InvalidOperationException("No se encontro la raiz de la solucion.");
    }
}

public sealed class CocosApiFactory(
    string connectionString,
    TimeProvider? timeProvider = null,
    ILoggerProvider? loggerProvider = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Cocos", connectionString);

        // Ambos overrides se registran DESPUES de los de Program.cs, asi que ganan al resolver.
        // Sin reemplazar el reloj, el PeriodicTimer del job espera cinco minutos reales y su
        // cuerpo no se ejecuta en ningun test.
        if (timeProvider is not null)
            builder.ConfigureServices(services => services.AddSingleton(timeProvider));

        if (loggerProvider is not null)
            builder.ConfigureLogging(logging => logging.AddProvider(loggerProvider));
    }
}

/// <summary>Base de cada clase de test: su propia base de datos y su propio host.</summary>
public abstract class IntegrationTestBase(PostgresFixture fixture) : IAsyncLifetime
{
    protected CocosApiFactory Factory { get; private set; } = null!;
    protected HttpClient Client { get; private set; } = null!;
    protected string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        ConnectionString = await fixture.CreateDatabaseAsync();
        Factory = new CocosApiFactory(ConnectionString);
        Client = Factory.CreateClient();
    }

    public Task DisposeAsync()
    {
        Client.Dispose();
        return Factory.DisposeAsync().AsTask();
    }
}
