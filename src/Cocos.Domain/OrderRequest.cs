using Cocos.Domain.Enums;

namespace Cocos.Domain;

/// <summary>
/// Una orden ya valuada y lista para decidirse: el precio resuelto (limite explicito o último
/// close) y la cantidad en nominales, haya venido como size exacto o derivada de un monto en
/// pesos.
///
/// De aca en adelante la orden se describe siempre igual: nadie vuelve a preguntarse si el size
/// viene o se calcula.
/// </summary>
public sealed record OrderRequest
{
    private OrderRequest() { }

    public int UserId { get; private init; }
    public int InstrumentId { get; private init; }
    public OrderSide Side { get; private init; }
    public OrderType Type { get; private init; }
    public int Size { get; private init; }
    public decimal Price { get; private init; }
    public string? IdempotencyKey { get; private init; }

    /// <summary>Pesos que compromete la orden si es una compra.</summary>
    public decimal Notional => Size * Price;

    /// <summary>
    /// Un monto que no alcanza para una sola accion da una orden de tamano cero, que no llega a
    /// formarse: no se persiste ni como rechazada. Es 400, no 201.
    /// </summary>
    public bool HasTradeableSize => Size > 0;

    /// <summary>
    /// Arma el pedido resolviendo la cantidad. Recibe primitivos y no el comando de la capa de
    /// aplicacion: el dominio no conoce esa capa.
    /// </summary>
    public static OrderRequest For(
        int userId,
        int instrumentId,
        OrderSide side,
        OrderType type,
        int? size,
        decimal? amount,
        decimal price,
        string? idempotencyKey) => new()
        {
            UserId = userId,
            InstrumentId = instrumentId,
            Side = side,
            Type = type,
            // El validador ya garantiza que viene exactamente uno de los dos.
            Size = size ?? OrderMath.SizeFromAmount(amount!.Value, price),
            Price = price,
            IdempotencyKey = idempotencyKey
        };
}
