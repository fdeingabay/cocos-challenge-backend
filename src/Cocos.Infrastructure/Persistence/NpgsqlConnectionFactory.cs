using System.Data.Common;
using Cocos.Application.Common;
using Npgsql;

namespace Cocos.Infrastructure.Persistence;

/// <summary>
/// Publica a proposito: Wolverine genera el codigo de invocacion de los handlers en tiempo
/// de compilacion y necesita poder referenciar el tipo concreto. Si es internal cae en
/// service location, que esta deshabilitado, y el handler falla en runtime.
/// </summary>
public sealed class NpgsqlConnectionFactory(NpgsqlDataSource dataSource) : IDbConnectionFactory
{
    public async Task<DbConnection> OpenAsync(CancellationToken cancellationToken)
        => await dataSource.OpenConnectionAsync(cancellationToken);
}
