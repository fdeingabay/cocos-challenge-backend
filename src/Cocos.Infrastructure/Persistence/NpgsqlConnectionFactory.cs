using System.Data.Common;
using Cocos.Application.Common;
using Npgsql;

namespace Cocos.Infrastructure.Persistence;

/// <summary>
/// Abre conexiones para el lado de LECTURA del sistema: las consultas Dapper que proyectan a
/// records. Ninguna escritura pasa por aca.
///
/// Pública a propósito: Wolverine genera en compilacion el código que invoca a los handlers y
/// necesita referenciar el tipo concreto. Si fuera internal caería en service location, que
/// esta deshabilitado, y el handler fallaría en runtime.
/// </summary>
public sealed class NpgsqlConnectionFactory(NpgsqlDataSource dataSource) : IDbConnectionFactory
{
    public async Task<DbConnection> OpenAsync(CancellationToken cancellationToken)
        => await dataSource.OpenConnectionAsync(cancellationToken);
}
