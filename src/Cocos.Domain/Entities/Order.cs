using Cocos.Domain.Enums;

namespace Cocos.Domain.Entities;

/// <summary>
/// Una orden es una maquina de estados, no un registro plano. La tabla "orders" cumple tres
/// roles a la vez: ledger contable (las ejecutadas determinan cash y tenencia), libro de
/// pendientes (las vivas) y log de transferencias (CASH_IN / CASH_OUT). Todo el estado del
/// usuario es una proyeccion de esta tabla.
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

    /// <summary>
    /// Cuando se cancelo, si se cancelo. Lo escribe el UPDATE condicional del libro, no el
    /// dominio: Cancel() produce el hecho y no lo aplica. Aca solo se lee.
    /// </summary>
    public DateTime? CancelledAt { get; private set; }

    public string? IdempotencyKey { get; private set; }

    /// <summary>Cantidad todavia no ejecutada: lo que la orden sigue reservando.</summary>
    public int RemainingSize => Size - FilledSize;

    /// <summary>Una orden viva sigue comprometiendo fondos (BUY) o nominales (SELL).</summary>
    public bool IsOpen => Status is OrderStatus.New or OrderStatus.PartiallyFilled;

    /// <summary>
    /// El enunciado habla solo de las NEW; tambien se cancelan las parcialmente ejecutadas,
    /// donde lo que se cancela es el remanente y nunca lo ya ejecutado.
    /// </summary>
    public bool CanBeCancelled => IsOpen;

    /// <summary>
    /// Cancela el remanente, nunca lo ya ejecutado: eso es un hecho consumado.
    ///
    /// No muta la orden a propósito: produce la cancelación como hecho para que el libro la
    /// registre en un solo acto, verificando ahi mismo que la orden siga viva. Mutar aca
    /// obligaria a un read-modify-write, y entre el read y el write entra otra cancelación que
    /// libera la reserva por segunda vez.
    ///
    /// Si la orden ya no es cancelable lanza <see cref="InvalidOperationException"/>: el caso de
    /// uso pregunta por CanBeCancelled antes, asi que llegar aca es un error de programacion y
    /// no un desenlace posible del negocio.
    /// </summary>
    public OrderCancellation Cancel(DateTime now)
    {
        if (!CanBeCancelled)
            throw new InvalidOperationException(
                $"La orden {Id} esta {Status} y no es cancelable. Preguntar por CanBeCancelled antes.");

        return new OrderCancellation(Id, UserId, now);
    }

    /// <summary>
    /// Una orden viva cuya jornada ya termino. El limite es inclusivo: en el instante exacto del
    /// vencimiento la orden ya vencio. Las MARKET nacen ejecutadas y sin ExpiresAt, asi que el
    /// criterio las descarta solo, sin preguntar por el tipo.
    /// </summary>
    public bool HasExpired(DateTime now) => IsOpen && ExpiresAt is { } expiresAt && expiresAt <= now;

    /// <summary>Valor monetario total de la orden segun lo solicitado.</summary>
    public decimal NotionalRequested => Size * Price;

    /// <summary>Valor monetario efectivamente movido.</summary>
    public decimal NotionalFilled => FilledSize * Price;

    /// <summary>Valor monetario todavia reservado por la orden.</summary>
    public decimal NotionalReserved => RemainingSize * Price;

    /// <summary>
    /// En que estado nace una orden. Es la única decision que define el resultado del envio, y
    /// vive aca y no en el handler: elegir cual de las tres formas de abajo corresponde es
    /// negocio, no orquestacion.
    ///
    /// Sin disponible se rechaza, sea cual sea el tipo. Con disponible, una MARKET se ejecuta en
    /// el acto y una LIMIT queda viva reservando hasta el cierre de la jornada.
    /// </summary>
    public static Order Place(OrderRequest request, bool hasFunds, DateTime now)
    {
        if (!hasFunds)
            return Rejected(request.UserId, request.InstrumentId, request.Side, request.Type,
                            request.Size, request.Price, now, request.IdempotencyKey);

        return request.Type == OrderType.Market
            ? Executed(request.UserId, request.InstrumentId, request.Side,
                       request.Size, request.Price, now, request.IdempotencyKey)
            : Open(request.UserId, request.InstrumentId, request.Side,
                   request.Size, request.Price, now, OrderMath.EndOfDay(now), request.IdempotencyKey);
    }

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
    /// Rechazo por fondos o tenencia insuficientes; el enunciado pide persistirla igual. No
    /// reserva ni mueve nada: una REJECTED nunca afecta cash ni tenencia.
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
