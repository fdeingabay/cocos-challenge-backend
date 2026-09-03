using System.Collections.Concurrent;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Cocos.IntegrationTests;

/// <summary>
/// El job de vencimiento visto desde afuera: no por el bus, sino dejando que el servicio
/// dispare su propio tick.
///
/// Lo que se prueba aca no es el barrido -- de eso se ocupa ExpireOrdersTests -- sino el
/// CABLEADO que solo existe en este servicio: que cree su scope de DI, que resuelva el bus
/// desde ese scope, y que un fallo puntual no lo mate. Un BackgroundService es singleton y
/// sus colaboradores son scoped, asi que ese scope es obligatorio: sin el, el job revienta
/// en el primer tick -- cinco minutos despues de arrancar, en produccion -- y toda la suite
/// sigue en verde.
///
/// El reloj entra por TimeProvider, que es exactamente para lo que el servicio lo toma.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class OrderExpirationServiceTests(PostgresFixture fixture) : IntegrationTestBase(fixture)
{
    /// <summary>Las dos NEW del seed (julio de 2023) nacen vencidas: son el trabajo a encontrar.</summary>
    private static readonly int[] NewDelSeed = [5, 7];

    /// <summary>El de appsettings.json. Se usa el real para que el test valide esa configuracion.</summary>
    private static readonly TimeSpan Intervalo = TimeSpan.FromMinutes(5);

    [Fact]
    public async Task El_tick_vence_las_ordenes_pasando_por_el_servicio_y_no_por_el_bus()
    {
        var reloj = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var host = Hospedar(reloj);

        (await Estados()).Should().AllBe("NEW", "todavia no corrio ningun tick");

        reloj.Advance(Intervalo);

        await EsperarA(async () => (await Estados()).All(e => e == "EXPIRED"),
            "el tick tiene que armar su scope, resolver el bus y correr el barrido");
    }

    [Fact]
    public async Task Un_barrido_que_falla_no_mata_el_servicio()
    {
        var reloj = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var log = new LogEnMemoria();
        await using var host = Hospedar(reloj, log);

        // Se rompe la tabla antes del primer tick: es un fallo real de base, que es contra lo
        // que el catch defiende. La alternativa -- pisar IOpenOrders en el contenedor -- no
        // sirve: Wolverine genera el código de invocacion en compilacion.
        await RenombrarOrders("orders", "orders_fuera_de_servicio");

        reloj.Advance(Intervalo);

        await EsperarA(() => Task.FromResult(log.Contiene("Fallo el barrido")),
            "el fallo se loguea; tragarlo en silencio dejaria el job muerto sin que nadie se entere");

        await RenombrarOrders("orders_fuera_de_servicio", "orders");

        (await Estados()).Should().AllBe("NEW", "el barrido que fallo no llego a escribir nada");

        reloj.Advance(Intervalo);

        await EsperarA(async () => (await Estados()).All(e => e == "EXPIRED"),
            "si el servicio hubiera muerto con la excepcion, este segundo tick no existiria");
    }

    // ---------- helpers ----------

    /// <summary>
    /// Un host propio con el reloj falso. No se reusa el de la clase base porque ese corre con
    /// TimeProvider.System: su timer espera cinco minutos reales y nunca dispara.
    /// </summary>
    private CocosApiFactory Hospedar(TimeProvider reloj, ILoggerProvider? log = null)
    {
        var factory = new CocosApiFactory(ConnectionString, reloj, log);

        // Fuerza el arranque del host: hasta que no se pide un cliente, el servicio no existe
        // y adelantar el reloj no dispararia nada.
        factory.CreateClient().Dispose();

        return factory;
    }

    private async Task<string[]> Estados()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);

        var estados = await connection.QueryAsync<string>(
            "SELECT status FROM orders WHERE id = ANY(@ids) ORDER BY id;", new { ids = NewDelSeed });

        return estados.ToArray();
    }

    private async Task RenombrarOrders(string desde, string hasta)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.ExecuteAsync($"ALTER TABLE {desde} RENAME TO {hasta};");
    }

    /// <summary>
    /// El tick es asincrono: adelantar el reloj lo dispara pero no espera a que termine. Se
    /// sondea con limite en vez de dormir un rato fijo, que seria lento y flaky a la vez.
    /// </summary>
    private static async Task EsperarA(Func<Task<bool>> condicion, string porque)
    {
        var limite = DateTime.UtcNow.AddSeconds(15);

        while (DateTime.UtcNow < limite)
        {
            if (await condicion()) return;
            await Task.Delay(25);
        }

        Assert.Fail($"Se agoto la espera: {porque}.");
    }

    /// <summary>Captura lo que el servicio loguea, que es la única senal observable del catch.</summary>
    private sealed class LogEnMemoria : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _entradas = new();

        public bool Contiene(string texto) => _entradas.Any(e => e.Contains(texto));

        public ILogger CreateLogger(string categoryName) => new Escritor(_entradas);

        public void Dispose() { }

        private sealed class Escritor(ConcurrentQueue<string> entradas) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
                => entradas.Enqueue(formatter(state, exception));
        }
    }
}
