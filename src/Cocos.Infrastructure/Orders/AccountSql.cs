using Cocos.Application.Common;
using Cocos.Application.Features.Orders.SubmitOrder;

namespace Cocos.Infrastructure.Orders;

/// <summary>
/// El SQL del envio de órdenes, en un solo lugar. Aca se decide contra el disponible, que es
/// el invariante del sistema: las consultas estan juntas para que se lean como un bloque.
/// </summary>
internal static class AccountSql
{
    /// <summary>
    /// Serializa la cuenta. Sin esto el conflicto vive en una SUMA y no en una fila, asi que
    /// Postgres no puede verlo: dos transacciones leen el mismo disponible, insertan filas
    /// distintas, no se pisan y ambas commitean (write skew). Ni REPEATABLE READ lo evita.
    /// Bloquear esta fila materializa el conflicto y lo vuelve detectable.
    /// </summary>
    public const string LockAccount =
        "SELECT 1 FROM user_accounts WHERE userid = @UserId FOR UPDATE;";

    public const string FindByIdempotencyKey =
        "SELECT id FROM orders WHERE userid = @UserId AND idempotencykey = @Key LIMIT 1;";

    /// <summary>
    /// Poder de compra: lo contable menos lo reservado por las órdenes de compra vivas. Sin el
    /// segundo término se puede retirar plata ya comprometida o duplicar una compra pendiente.
    ///
    /// Los dos terminos salen de LedgerSql, compartidos con el portfolio: es la MISMA cuenta que
    /// la API informa como disponible, y tiene que serlo -- si divergen, el sistema acepta un
    /// número distinto del que informó.
    /// </summary>
    public const string AvailableCash =
        $"""
         SELECT {LedgerSql.AccountingCash} - {LedgerSql.ReservedCash}
         FROM orders
         WHERE userid = @UserId;
         """;

    /// <summary>
    /// Nominales libres: los de cartera menos los reservados por las ventas vivas.
    /// </summary>
    public const string AvailableQuantity =
        $"""
         SELECT {LedgerSql.ExecutedQuantity} - {LedgerSql.ReservedQuantity}
         FROM orders
         WHERE userid = @UserId AND instrumentid = @InstrumentId;
         """;

    public const string OrderById =
        """
        SELECT o.id AS Id, o.userid AS UserId, o.instrumentid AS InstrumentId, i.ticker AS Ticker,
               o.side AS Side, o.type AS Type, o.status AS Status, o.size AS Size,
               o.filledsize AS FilledSize, o.price AS Price, o.datetime AS DateTime,
               o.expiresat AS ExpiresAt
        FROM orders o
        JOIN instruments i ON i.id = o.instrumentid
        WHERE o.id = @Id;
        """;

    public const string Instrument =
        "SELECT id AS Id, ticker AS Ticker, type AS Type FROM instruments WHERE id = @InstrumentId;";

    public const string LastClose =
        """
        SELECT m."close"
        FROM marketdata m
        WHERE m.instrumentid = @InstrumentId
        ORDER BY m."date" DESC
        LIMIT 1;
        """;
}

/// <summary>Fila cruda de la orden que se relee cuando la clave de idempotencia ya existía.</summary>
internal sealed record OrderRow(
    int Id, int UserId, int InstrumentId, string Ticker, string Side, string Type,
    string Status, int Size, int FilledSize, decimal Price, DateTime DateTime, DateTime? ExpiresAt)
{
    public SubmitOrderResponse ToResponse() => new(
        Id, UserId, InstrumentId, Ticker, Side, Type, Status,
        Size, FilledSize, Price, Size * Price, DateTime, ExpiresAt,
        SubmitOrderResponse.ReasonForStatus(Status));
}
