using Cocos.Domain.Enums;

namespace Cocos.Domain;

/// <summary>
/// Traduccion entre los enums del dominio y los literales que ya viven en la base
/// provista por el challenge. Se mantienen exactamente esos literales para no romper
/// los datos existentes ni las expectativas del evaluador.
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

    public static OrderStatus ToOrderStatus(string value) => value switch
    {
        "NEW" => OrderStatus.New,
        "PARTIALLY_FILLED" => OrderStatus.PartiallyFilled,
        "FILLED" => OrderStatus.Filled,
        "REJECTED" => OrderStatus.Rejected,
        "CANCELLED" => OrderStatus.Cancelled,
        "EXPIRED" => OrderStatus.Expired,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Status desconocido.")
    };

    /// <summary>Estados en los que una orden sigue viva y por lo tanto sigue reservando.</summary>
    public static readonly string[] OpenStatuses = ["NEW", "PARTIALLY_FILLED"];

    /// <summary>Estados en los que una orden ya movio cash o tenencia reales.</summary>
    public static readonly string[] ExecutedStatuses = ["FILLED", "PARTIALLY_FILLED"];
}
