namespace Cocos.Application.Features.Orders.CancelOrder;

public sealed record CancelOrderCommand(int OrderId, int UserId);

public sealed record CancelOrderResponse(int Id, string Status, DateTime CancelledAt);
