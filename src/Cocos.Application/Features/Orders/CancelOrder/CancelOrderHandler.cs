using Cocos.Application.Common;
using Cocos.Domain;

namespace Cocos.Application.Features.Orders.CancelOrder;

/// <summary>
/// Cancelacion de una orden viva. La reserva se libera sola: el disponible se calcula sobre las
/// órdenes vivas, asi que al dejar de estarlo la orden deja de restar.
///
/// El caso de uso pregunta dos veces si la orden es cancelable y las dos hacen falta:
/// CanBeCancelled para EXPLICAR el rechazo con el estado real, y el WHERE de la escritura para
/// GARANTIZAR que la reserva no se libere dos veces. La primera puede quedar vieja entre la
/// lectura y la escritura; la segunda no.
/// </summary>
public static class CancelOrderHandler
{
    public static async Task<Result<CancelOrderResponse>> Handle(
        CancelOrderCommand command,
        IOrderBook orders,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var order = await orders.FindAsync(command.OrderId, command.UserId, cancellationToken);

        if (order is null)
            return Error.NotFound("order.not_found",
                $"No existe la orden {command.OrderId} para el usuario {command.UserId}.");

        if (!order.CanBeCancelled)
            return Error.Conflict("order.not_cancellable", CancellationRefusal.For(order));

        var cancellation = order.Cancel(timeProvider.GetUtcNow().UtcDateTime);

        // Entre la lectura y aca la orden pudo dejar de estar viva: la vencio el barrido, o el
        // usuario la cancelo desde otra pestana. El libro la registra solo si sigue viendola
        // viva, asi que dos cancelaciones simultaneas no liberan la reserva dos veces.
        if (!await orders.ApplyAsync(cancellation))
            return Error.Conflict("order.no_longer_open",
                $"La orden {order.Id} dejo de estar viva mientras se cancelaba.");

        return CancelOrderResponse.For(cancellation);
    }
}
