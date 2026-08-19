using Cocos.Domain.Enums;

namespace Cocos.Application.Features.Orders.SubmitOrder;

/// <param name="Size">Cantidad exacta de acciones. Excluyente con Amount.</param>
/// <param name="Amount">Monto total a invertir en pesos; se traduce a la cantidad maxima de acciones enteras. Excluyente con Size.</param>
/// <param name="Price">Precio limite. Obligatorio para LIMIT, ignorado para MARKET (que usa el ultimo close).</param>
/// <param name="IdempotencyKey">Clave para que un reintento del cliente no cree una segunda orden.</param>
public sealed record SubmitOrderCommand(
    int UserId,
    int InstrumentId,
    OrderSide Side,
    OrderType Type,
    int? Size,
    decimal? Amount,
    decimal? Price,
    string? IdempotencyKey);

/// <param name="Notional">Valor monetario de la orden segun lo solicitado.</param>
/// <param name="RejectionReason">Solo presente cuando el status es REJECTED.</param>
public sealed record SubmitOrderResponse(
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
    string? RejectionReason);
