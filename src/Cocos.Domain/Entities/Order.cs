using Cocos.Domain.Enums;

namespace Cocos.Domain.Entities;

/// <summary>
/// Una orden es una maquina de estados, no un registro plano. En este sistema la tabla
/// "orders" cumple tres roles a la vez: ledger contable (las ejecutadas determinan cash y
/// tenencia), libro de ordenes pendientes (las vivas) y log de transferencias
/// (CASH_IN / CASH_OUT). Todo el estado del usuario es una proyeccion de esta tabla.
/// </summary>
public sealed class Order
{
    private Order() { } // EF Core

    public int Id { get; private set; }
    public int InstrumentId { get; private set; }
    public int UserId { get; private set; }

    /// <summary>Cantidad solicitada. Para CASH_IN / CASH_OUT es el monto en pesos (con Price = 1).</summary>
    public int Size { get; private set; }

    /// <summary>Cantidad efectivamente ejecutada. Es la que cuenta para tenencia y cash.</summary>
    public int FilledSize { get; private set; }

    public decimal Price { get; private set; }
    public OrderType Type { get; private set; }
    public OrderSide Side { get; private set; }
    public OrderStatus Status { get; private set; }
    public DateTime DateTime { get; private set; }
    public DateTime? ExpiresAt { get; private set; }
    public string? IdempotencyKey { get; private set; }

    /// <summary>Cantidad todavia no ejecutada: lo que la orden sigue reservando.</summary>
    public int RemainingSize => Size - FilledSize;

    /// <summary>Una orden viva sigue comprometiendo fondos (BUY) o nominales (SELL).</summary>
    public bool IsOpen => Status is OrderStatus.New or OrderStatus.PartiallyFilled;

    /// <summary>
    /// El enunciado dice "solo se pueden cancelar las NEW". Lo extendemos a las parcialmente
    /// ejecutadas: se cancela el remanente, nunca lo ya ejecutado, que es un hecho consumado.
    /// </summary>
    public bool CanBeCancelled => IsOpen;

    /// <summary>Valor monetario total de la orden segun lo solicitado.</summary>
    public decimal NotionalRequested => Size * Price;

    /// <summary>Valor monetario efectivamente movido.</summary>
    public decimal NotionalFilled => FilledSize * Price;

    /// <summary>Valor monetario todavia reservado por la orden.</summary>
    public decimal NotionalReserved => RemainingSize * Price;

    /// <summary>Orden MARKET: se ejecuta en el acto y por el total.</summary>
    public static Order Executed(
        int userId, int instrumentId, OrderSide side, int size, decimal price,
        DateTime timestamp, string? idempotencyKey = null) => new()
    {
        UserId = userId,
        InstrumentId = instrumentId,
        Side = side,
        Size = size,
        FilledSize = size,
        Price = price,
        Type = OrderType.Market,
        Status = OrderStatus.Filled,
        DateTime = timestamp,
        ExpiresAt = null,
        IdempotencyKey = idempotencyKey
    };

    /// <summary>Orden LIMIT: queda viva en el libro reservando, sin mover nada todavia.</summary>
    public static Order Open(
        int userId, int instrumentId, OrderSide side, int size, decimal price,
        DateTime timestamp, DateTime expiresAt, string? idempotencyKey = null) => new()
    {
        UserId = userId,
        InstrumentId = instrumentId,
        Side = side,
        Size = size,
        FilledSize = 0,
        Price = price,
        Type = OrderType.Limit,
        Status = OrderStatus.New,
        DateTime = timestamp,
        ExpiresAt = expiresAt,
        IdempotencyKey = idempotencyKey
    };

    /// <summary>
    /// Rechazo por fondos o tenencia insuficientes. El enunciado exige persistirla.
    /// No reserva ni mueve nada: una REJECTED jamas puede afectar cash ni tenencia.
    /// </summary>
    public static Order Rejected(
        int userId, int instrumentId, OrderSide side, OrderType type, int size, decimal price,
        DateTime timestamp, string? idempotencyKey = null) => new()
    {
        UserId = userId,
        InstrumentId = instrumentId,
        Side = side,
        Size = size,
        FilledSize = 0,
        Price = price,
        Type = type,
        Status = OrderStatus.Rejected,
        DateTime = timestamp,
        ExpiresAt = null,
        IdempotencyKey = idempotencyKey
    };
}
