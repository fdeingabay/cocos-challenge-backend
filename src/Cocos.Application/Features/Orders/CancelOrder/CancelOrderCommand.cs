using Cocos.Domain;

namespace Cocos.Application.Features.Orders.CancelOrder;

public sealed record CancelOrderCommand(int OrderId, int UserId);

public sealed record CancelOrderResponse(int Id, string Status, DateTime CancelledAt)
{
    /// <summary>
    /// El status viaja al cliente como el literal de la base ("CANCELLED"), igual que en toda la
    /// API. La traduccion vive aca para que el handler hable solo de negocio.
    /// </summary>
    public static CancelOrderResponse For(OrderCancellation cancellation) =>
        new(cancellation.OrderId, cancellation.Status.ToDb(), cancellation.CancelledAt);
}
