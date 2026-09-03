using Cocos.Application.Common;
using Cocos.Application.Features.Orders.ExpireOrders;
using Cocos.Domain;
using Cocos.Infrastructure.Persistence.Configurations;
using Dapper;
using Microsoft.EntityFrameworkCore;

namespace Cocos.Infrastructure.Orders;

/// <summary>
/// El barrido de vencimiento contra Postgres.
///
/// Toma el ICocosDbContext y no el IDbConnectionFactory aunque no comparta transacción con
/// nadie: el factory es el lado de LECTURA del sistema, y toda escritura pasa por el contexto.
/// </summary>
public sealed class OpenOrders(ICocosDbContext db) : IOpenOrders
{
    public async Task<int> ApplyAsync(OrderExpiry expiry)
        // Con CancellationToken.None: es un commit de datos. Si el host se apaga conviene
        // terminar este statement -- corto y atómico -- antes que dejar el barrido por la mitad
        // y tener que averiguar despues que quedó vencido y que no.
        => await db.Database.GetDbConnection().ExecuteAsync(new CommandDefinition(
            OrderBookSql.ExpireOpen,
            new
            {
                // Sin traducir el Kind, la comparación "expiresat <= @AsOf" enfrenta una
                // columna sin zona contra un timestamptz y Postgres resuelve la diferencia con
                // el TimeZone de la SESIÓN: correcto solo mientras el server este en UTC.
                AsOf = TimestampConverters.ToDb(expiry.AsOf),
                NewStatus = expiry.Status.ToDb(),
                OpenStatuses = DbValues.OpenStatuses
            },
            cancellationToken: CancellationToken.None));
}
