using Cocos.Application.Common;
using Cocos.Application.Features.Orders.CancelOrder;
using Cocos.Domain;
using Cocos.Domain.Entities;
using Cocos.Infrastructure.Persistence.Configurations;
using Dapper;
using Microsoft.EntityFrameworkCore;

namespace Cocos.Infrastructure.Orders;

/// <summary>
/// El libro de órdenes contra Postgres. Lee con EF -- hay que materializar la entidad para que
/// el dominio decida -- y escribe con la sentencia condicional de OrderBookSql.
/// </summary>
public sealed class OrderBook(ICocosDbContext db) : IOrderBook
{
    // AsNoTracking a propósito: la orden se lee para DECIDIR, no para mutarla. Sin esto alguien
    // puede creer que alcanza con tocar la entidad y llamar a SaveChanges, que es justo el
    // read-modify-write que este diseño evita.
    public Task<Order?> FindAsync(int orderId, int userId, CancellationToken cancellationToken)
        => db.Orders.AsNoTracking()
             .FirstOrDefaultAsync(o => o.Id == orderId && o.UserId == userId, cancellationToken);

    public async Task<bool> ApplyAsync(OrderCancellation cancellation)
    {
        // Con CancellationToken.None: aunque el cliente ya no espere respuesta, la decisión de
        // cancelar esta tomada y no registrarla deja la reserva retenida para siempre.
        var affected = await db.Database.GetDbConnection().ExecuteAsync(new CommandDefinition(
            OrderBookSql.CancelIfOpen,
            new
            {
                cancellation.OrderId,
                cancellation.UserId,
                NewStatus = cancellation.Status.ToDb(),
                // El Kind se traduce aca porque Dapper no pasa por los converters de EF: la
                // columna es timestamp sin zona y Npgsql infiere timestamptz de un Kind=Utc.
                CancelledAt = TimestampConverters.ToDb(cancellation.CancelledAt),
                OpenStatuses = DbValues.OpenStatuses
            },
            cancellationToken: CancellationToken.None));

        return affected == 1;
    }
}
