using Cocos.Application.Common;
using Cocos.Domain;
using Cocos.Domain.Entities;
using Cocos.Domain.Enums;
using Dapper;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Cocos.Application.Features.Orders.SubmitOrder;

/// <summary>
/// Envio de una orden al mercado. Todo el trabajo ocurre dentro de UNA transaccion cuyo
/// primer paso es tomar el lock de la cuenta: validar e insertar tienen que ser una sola
/// operacion indivisible, o dos requests concurrentes del mismo usuario pueden gastar el
/// mismo peso dos veces.
/// </summary>
public static class SubmitOrderHandler
{
    // Serializa la cuenta. Sin esto el conflicto vive en una SUMA, no en ninguna fila, y
    // por lo tanto Postgres no puede detectarlo: dos transacciones leen el mismo disponible,
    // insertan filas distintas, no colisionan y ambas commitean (write skew). Ni siquiera
    // REPEATABLE READ lo evita. Bloquear esta fila materializa el conflicto.
    private const string LockAccountSql =
        "SELECT 1 FROM user_accounts WHERE userid = @UserId FOR UPDATE;";

    private const string FindByIdempotencyKeySql =
        "SELECT id FROM orders WHERE userid = @UserId AND idempotencykey = @Key LIMIT 1;";

    private const string InstrumentSql =
        "SELECT id AS Id, ticker AS Ticker, type AS Type FROM instruments WHERE id = @InstrumentId;";

    private const string LastCloseSql =
        """
        SELECT m."close"
        FROM marketdata m
        WHERE m.instrumentid = @InstrumentId
        ORDER BY m."date" DESC
        LIMIT 1;
        """;

    // Pesos que el usuario realmente puede comprometer: lo contable menos lo que ya esta
    // reservado por ordenes de compra vivas. Omitir el segundo termino es exactamente el
    // bug que permite retirar plata comprometida o duplicar una compra pendiente.
    private const string AvailableCashSql =
        """
        SELECT
          COALESCE(SUM(CASE
              WHEN side = 'CASH_IN'  AND status = 'FILLED' THEN  size * price
              WHEN side = 'CASH_OUT' AND status = 'FILLED' THEN -size * price
              WHEN side = 'SELL' AND status IN ('FILLED','PARTIALLY_FILLED') THEN  filledsize * price
              WHEN side = 'BUY'  AND status IN ('FILLED','PARTIALLY_FILLED') THEN -filledsize * price
              ELSE 0 END), 0)
          -
          COALESCE(SUM(CASE
              WHEN side = 'BUY' AND status IN ('NEW','PARTIALLY_FILLED') THEN (size - filledsize) * price
              ELSE 0 END), 0)
        FROM orders
        WHERE userid = @UserId;
        """;

    // Nominales libres del instrumento: los de cartera menos los reservados por ventas vivas.
    private const string AvailableQuantitySql =
        """
        SELECT
          COALESCE(SUM(CASE
              WHEN side = 'BUY'  AND status IN ('FILLED','PARTIALLY_FILLED') THEN  filledsize
              WHEN side = 'SELL' AND status IN ('FILLED','PARTIALLY_FILLED') THEN -filledsize
              ELSE 0 END), 0)
          -
          COALESCE(SUM(CASE
              WHEN side = 'SELL' AND status IN ('NEW','PARTIALLY_FILLED') THEN size - filledsize
              ELSE 0 END), 0)
        FROM orders
        WHERE userid = @UserId AND instrumentid = @InstrumentId;
        """;

    private const string OrderByIdSql =
        """
        SELECT o.id AS Id, o.userid AS UserId, o.instrumentid AS InstrumentId, i.ticker AS Ticker,
               o.side AS Side, o.type AS Type, o.status AS Status, o.size AS Size,
               o.filledsize AS FilledSize, o.price AS Price, o.datetime AS DateTime,
               o.expiresat AS ExpiresAt
        FROM orders o
        JOIN instruments i ON i.id = o.instrumentid
        WHERE o.id = @Id;
        """;

    public static async Task<Result<SubmitOrderResponse>> Handle(
        SubmitOrderCommand command,
        ICocosDbContext db,
        IValidator<SubmitOrderCommand> validator,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(command, cancellationToken);
        if (!validation.IsValid)
            return Error.Validation("order.invalid",
                string.Join(" ", validation.Errors.Select(e => e.ErrorMessage)));

        var now = timeProvider.GetUtcNow().UtcDateTime;

        // Normalizacion explicita: "sin clave" y "clave en blanco" son lo mismo, y tienen que
        // llegar a la base como NULL. Hoy el binding de ASP.NET ya colapsa el header vacio a
        // null, pero apoyarse en eso seria depender del transporte: si el comando llegara por
        // otra via (un consumer, un test directo sobre el handler) una clave en blanco pasaria
        // el chequeo de duplicados y se persistiria igual, y dos de esas colisionarian contra
        // el indice unico parcial -- que solo excluye NULL, no la cadena vacia.
        var idempotencyKey = string.IsNullOrWhiteSpace(command.IdempotencyKey)
            ? null
            : command.IdempotencyKey.Trim();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var connection = db.Database.GetDbConnection();
        var dbTransaction = transaction.GetDbTransaction();

        // --- 1. Lock de la cuenta -------------------------------------------------------
        var accountExists = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            LockAccountSql, new { command.UserId }, dbTransaction, cancellationToken: cancellationToken));

        if (accountExists is null)
            return Error.NotFound("user.not_found", $"No existe el usuario {command.UserId}.");

        // --- 2. Idempotencia ------------------------------------------------------------
        // Se consulta DENTRO del lock: dos reintentos simultaneos se serializan aca, asi que
        // el segundo ve la orden que creo el primero en vez de insertar una duplicada.
        if (idempotencyKey is not null)
        {
            var existingId = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
                FindByIdempotencyKeySql,
                new { command.UserId, Key = idempotencyKey },
                dbTransaction, cancellationToken: cancellationToken));

            if (existingId is not null)
            {
                var replay = await LoadAsync(connection, dbTransaction, existingId.Value, cancellationToken);
                return Result.Success(replay with { RejectionReason = ReasonFor(replay.Status) });
            }
        }

        // --- 3. Instrumento y precio ----------------------------------------------------
        var instrument = await connection.QuerySingleOrDefaultAsync<InstrumentRow>(new CommandDefinition(
            InstrumentSql, new { command.InstrumentId }, dbTransaction, cancellationToken: cancellationToken));

        if (instrument is null)
            return Error.NotFound("instrument.not_found", $"No existe el instrumento {command.InstrumentId}.");

        if (instrument.Type == Instrument.CurrencyType)
            return Error.Validation("instrument.not_tradable",
                "El instrumento es una moneda; el cash se mueve con CASH_IN / CASH_OUT, no con ordenes de mercado.");

        decimal price;
        if (command.Type == OrderType.Limit)
        {
            price = command.Price!.Value;
        }
        else
        {
            var close = await connection.ExecuteScalarAsync<decimal?>(new CommandDefinition(
                LastCloseSql, new { command.InstrumentId }, dbTransaction, cancellationToken: cancellationToken));

            if (close is null or <= 0m)
                return Error.Validation("instrument.no_market_price",
                    $"El instrumento {instrument.Ticker} no tiene precio de mercado disponible.");

            price = close.Value;
        }

        // --- 4. Cantidad ----------------------------------------------------------------
        var size = command.Size ?? OrderMath.SizeFromAmount(command.Amount!.Value, price);

        if (size <= 0)
            return Error.Validation("order.size_zero",
                $"El monto enviado no alcanza para comprar ni una accion a {price:0.00}. No se persiste la orden porque no llega a formarse.");

        // --- 5. Disponible, ya con el lock tomado ---------------------------------------
        var isBuy = command.Side == OrderSide.Buy;

        var hasFunds = isBuy
            ? await connection.ExecuteScalarAsync<decimal>(new CommandDefinition(
                  AvailableCashSql, new { command.UserId },
                  dbTransaction, cancellationToken: cancellationToken)) >= size * price
            : await connection.ExecuteScalarAsync<int>(new CommandDefinition(
                  AvailableQuantitySql, new { command.UserId, command.InstrumentId },
                  dbTransaction, cancellationToken: cancellationToken)) >= size;

        // --- 6. Resultado ---------------------------------------------------------------
        Order order;
        string? rejectionReason = null;

        if (!hasFunds)
        {
            rejectionReason = isBuy
                ? "Pesos disponibles insuficientes. El disponible descuenta lo reservado por ordenes de compra vivas."
                : "Acciones disponibles insuficientes. El disponible descuenta lo reservado por ordenes de venta vivas.";

            order = Order.Rejected(command.UserId, command.InstrumentId, command.Side, command.Type,
                                   size, price, now, idempotencyKey);
        }
        else if (command.Type == OrderType.Market)
        {
            order = Order.Executed(command.UserId, command.InstrumentId, command.Side,
                                   size, price, now, idempotencyKey);
        }
        else
        {
            order = Order.Open(command.UserId, command.InstrumentId, command.Side,
                               size, price, now, OrderMath.EndOfDay(now), idempotencyKey);
        }

        db.Orders.Add(order);

        // A partir de aca NO se propaga el CancellationToken del request. Que el cliente
        // corte la conexion no puede dejar una orden aplicada a medias: es el caso de
        // "partial completion is dangerous". El trabajo pendiente se termina siempre.
        await db.SaveChangesAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);

        return Result.Success(new SubmitOrderResponse(
            order.Id, order.UserId, order.InstrumentId, instrument.Ticker,
            order.Side.ToDb(), order.Type.ToDb(), order.Status.ToDb(),
            order.Size, order.FilledSize, order.Price, order.NotionalRequested,
            order.DateTime, order.ExpiresAt, rejectionReason));
    }

    private static async Task<SubmitOrderResponse> LoadAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        int orderId,
        CancellationToken cancellationToken)
    {
        var row = await connection.QuerySingleAsync<OrderRow>(new CommandDefinition(
            OrderByIdSql, new { Id = orderId }, transaction, cancellationToken: cancellationToken));

        return new SubmitOrderResponse(
            row.Id, row.UserId, row.InstrumentId, row.Ticker, row.Side, row.Type, row.Status,
            row.Size, row.FilledSize, row.Price, row.Size * row.Price, row.DateTime, row.ExpiresAt, null);
    }

    private static string? ReasonFor(string status) => status == "REJECTED"
        ? "Orden rechazada por fondos o tenencia insuficientes."
        : null;

    private sealed record InstrumentRow(int Id, string Ticker, string Type);

    private sealed record OrderRow(
        int Id, int UserId, int InstrumentId, string Ticker, string Side, string Type,
        string Status, int Size, int FilledSize, decimal Price, DateTime DateTime, DateTime? ExpiresAt);
}
