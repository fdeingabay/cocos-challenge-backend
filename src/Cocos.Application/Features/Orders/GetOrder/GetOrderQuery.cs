namespace Cocos.Application.Features.Orders.GetOrder;

public sealed record GetOrderQuery(int OrderId, int UserId);

/// <summary>
/// Una orden puntual. Es el recurso al que apunta el Location del 201 del alta, que si no
/// existiera prometeria una direccion que no lleva a ningun lado.
/// </summary>
/// <param name="Notional">Valor monetario de la orden segun lo solicitado.</param>
/// <param name="CancelledAt">Cuando se cancelo, si se cancelo.</param>
public sealed record GetOrderResponse(
    int Id,
    int UserId,
    int InstrumentId,
    string Ticker,
    string Side,
    string Type,
    string Status,
    int Size,
    int FilledSize,
    decimal Price,
    decimal Notional,
    DateTime DateTime,
    DateTime? ExpiresAt,
    DateTime? CancelledAt);
