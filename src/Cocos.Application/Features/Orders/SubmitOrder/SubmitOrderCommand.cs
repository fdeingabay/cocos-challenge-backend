using Cocos.Domain;
using Cocos.Domain.Entities;
using Cocos.Domain.Enums;

namespace Cocos.Application.Features.Orders.SubmitOrder;

/// <param name="Size">Cantidad exacta de acciones. Excluyente con Amount.</param>
/// <param name="Amount">Monto total a invertir en pesos; se traduce a la cantidad máxima de acciones enteras. Excluyente con Size.</param>
/// <param name="Price">Precio limite. Obligatorio para LIMIT, ignorado para MARKET (que usa el último close).</param>
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
    string? RejectionReason)
{
    /// <summary>
    /// Traduce la orden recien creada al contrato de salida. El motivo sale del estado y no de
    /// una bandera aparte: si la orden nacio rechazada fue por falta de disponible, que es el
    /// unico rechazo que este caso de uso produce.
    /// </summary>
    public static SubmitOrderResponse For(Order order, string ticker) => new(
        order.Id, order.UserId, order.InstrumentId, ticker,
        order.Side.ToDb(), order.Type.ToDb(), order.Status.ToDb(),
        order.Size, order.FilledSize, order.Price, order.NotionalRequested,
        order.DateTime, order.ExpiresAt,
        order.Status == OrderStatus.Rejected ? Cocos.Domain.RejectionReason.For(order.Side) : null);

    /// <summary>
    /// Motivo para una orden releida de la base por idempotencia. Ahi se conoce el literal del
    /// estado pero no el lado, asi que el mensaje es forzosamente mas generico.
    /// </summary>
    public static string? ReasonForStatus(string status) => status == "REJECTED"
        ? "Orden rechazada por fondos o tenencia insuficientes."
        : null;
}

/// <summary>
/// Como termino el envio. El caso de uso tiene DOS desenlaces exitosos: se creo una orden nueva,
/// o se devolvio la que ya habia creado un intento anterior con la misma Idempotency-Key.
///
/// La distincion es de negocio y no de transporte. "Tu orden se registro recien" y "tu orden ya
/// estaba registrada" son hechos distintos, y el cliente que reintenta necesita saber cual le
/// toco: si los dos le llegan como un alta, contar altas cuenta dos veces la misma compra, que
/// es justo lo que la clave existe para impedir. Traducirlos a 201 y 200 es tarea del
/// controller.
/// </summary>
public sealed record SubmitOrderOutcome
{
    private SubmitOrderOutcome(SubmitOrderResponse order, bool isReplay)
    {
        Order = order;
        IsReplay = isReplay;
    }

    public SubmitOrderResponse Order { get; }

    /// <summary>true si la orden ya existia y este envio no creo nada.</summary>
    public bool IsReplay { get; }

    /// <summary>La orden se acaba de crear.</summary>
    public static SubmitOrderOutcome Placed(SubmitOrderResponse order) => new(order, false);

    /// <summary>La orden ya existia: este envio es el reintento de uno anterior.</summary>
    public static SubmitOrderOutcome Replayed(SubmitOrderResponse order) => new(order, true);
}
