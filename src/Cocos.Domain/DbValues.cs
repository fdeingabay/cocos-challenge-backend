using Cocos.Domain.Enums;

namespace Cocos.Domain;

/// <summary>
/// Traduccion entre los enums del dominio y los literales que ya viven en la base provista
/// por el challenge.
/// </summary>
public static class DbValues
{
    public static string ToDb(this OrderSide side) => side switch
    {
        OrderSide.Buy => "BUY",
        OrderSide.Sell => "SELL",
        OrderSide.CashIn => "CASH_IN",
        OrderSide.CashOut => "CASH_OUT",
        _ => throw new ArgumentOutOfRangeException(nameof(side), side, null)
    };

    public static string ToDb(this OrderType type) => type switch
    {
        OrderType.Market => "MARKET",
        OrderType.Limit => "LIMIT",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
    };

    public static string ToDb(this OrderStatus status) => status switch
    {
        OrderStatus.New => "NEW",
        OrderStatus.PartiallyFilled => "PARTIALLY_FILLED",
        OrderStatus.Filled => "FILLED",
        OrderStatus.Rejected => "REJECTED",
        OrderStatus.Cancelled => "CANCELLED",
        OrderStatus.Expired => "EXPIRED",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    public static OrderSide ToOrderSide(string value) => value switch
    {
        "BUY" => OrderSide.Buy,
        "SELL" => OrderSide.Sell,
        "CASH_IN" => OrderSide.CashIn,
        "CASH_OUT" => OrderSide.CashOut,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Side desconocido.")
    };

    public static OrderType ToOrderType(string value) => value switch
    {
        "MARKET" => OrderType.Market,
        "LIMIT" => OrderType.Limit,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Type desconocido.")
    };

    /// <summary>
    /// Traduce un literal leido de la base. Si el valor no existe lanza
    /// <see cref="ArgumentOutOfRangeException"/>: quiere decir que en la columna hay algo que el
    /// dominio no modela, y eso es un fallo, no un caso de negocio. Para el texto que manda el
    /// usuario esta ToOrderStatusOrNull.
    /// </summary>
    public static OrderStatus ToOrderStatus(string value) => ToOrderStatusOrNull(value)
        ?? throw new ArgumentOutOfRangeException(nameof(value), value, "Status desconocido.");

    /// <summary>
    /// Traduce un literal que puede no existir, sin lanzar nada: es la puerta de entrada del
    /// texto que viene de afuera, donde un status invalido es un error del cliente.
    ///
    /// Devuelve null en vez de seguir el patrón Try con un out porque el default de OrderStatus
    /// es New: quien ignorara el bool se quedaria filtrando por NEW ante un literal invalido,
    /// justo el error que este tipo evita. Sin out param no se puede escribir.
    /// </summary>
    public static OrderStatus? ToOrderStatusOrNull(string value) => value switch
    {
        "NEW" => OrderStatus.New,
        "PARTIALLY_FILLED" => OrderStatus.PartiallyFilled,
        "FILLED" => OrderStatus.Filled,
        "REJECTED" => OrderStatus.Rejected,
        "CANCELLED" => OrderStatus.Cancelled,
        "EXPIRED" => OrderStatus.Expired,
        _ => null
    };

    /// <summary>Estados en los que una orden sigue viva y por lo tanto sigue reservando.</summary>
    public static readonly string[] OpenStatuses = ["NEW", "PARTIALLY_FILLED"];

    // No hay lista de estados "ejecutados" y es a propósito: lo ejecutado es filledsize, no un
    // conjunto de estados. Una orden cancelada o vencida a medio ejecutar conserva lo que si se
    // ejecuto, y filtrar por estado le borraria al usuario las acciones que efectivamente compro.
}
