using Cocos.Application.Common;
using Cocos.Domain;
using Dapper;

namespace Cocos.Application.Features.Portfolio.GetPortfolio;

public static class GetPortfolioHandler
{
    // Dos statements, un solo round trip. El cash es un escalar sobre todas las ordenes;
    // las posiciones son un agregado por instrumento. Separarlos evita el CASE anidado que
    // haria falta para calcular ambos en una sola pasada, sin pagar un viaje extra a la base.
    //
    // Ojo con el orden de los WHEN: un CASE se corta en la primera rama verdadera. Por eso
    // lo ejecutado y lo reservado viven en SUM() distintos: una orden PARTIALLY_FILLED
    // aporta a los dos a la vez, y con un unico CASE la segunda contribucion se perderia.
    private const string CashSql =
        """
        SELECT
          COALESCE(SUM(CASE
              WHEN side = 'CASH_IN'  AND status = 'FILLED' THEN  size * price
              WHEN side = 'CASH_OUT' AND status = 'FILLED' THEN -size * price
              WHEN side = 'SELL' AND status IN ('FILLED','PARTIALLY_FILLED') THEN  filledsize * price
              WHEN side = 'BUY'  AND status IN ('FILLED','PARTIALLY_FILLED') THEN -filledsize * price
              ELSE 0 END), 0) AS Accounting,
          COALESCE(SUM(CASE
              WHEN side = 'BUY' AND status IN ('NEW','PARTIALLY_FILLED') THEN (size - filledsize) * price
              ELSE 0 END), 0) AS Reserved
        FROM orders
        WHERE userid = @UserId;
        """;

    // El LEFT JOIN LATERAL trae UNA fila de marketdata por instrumento: la mas reciente.
    // Un JOIN comun duplicaria cada posicion (hay 2 dias cargados por instrumento) y
    // resolverlo con una consulta por posicion seria el N+1 clasico.
    private const string PositionsSql =
        """
        WITH agg AS (
            SELECT o.instrumentid,
                   SUM(CASE WHEN o.side = 'BUY'  AND o.status IN ('FILLED','PARTIALLY_FILLED') THEN  o.filledsize
                            WHEN o.side = 'SELL' AND o.status IN ('FILLED','PARTIALLY_FILLED') THEN -o.filledsize
                            ELSE 0 END) AS quantity,
                   SUM(CASE WHEN o.side = 'SELL' AND o.status IN ('NEW','PARTIALLY_FILLED')
                            THEN o.size - o.filledsize ELSE 0 END) AS reserved,
                   SUM(CASE WHEN o.side = 'BUY' AND o.status IN ('FILLED','PARTIALLY_FILLED')
                            THEN o.filledsize * o.price ELSE 0 END) AS buycost,
                   SUM(CASE WHEN o.side = 'BUY' AND o.status IN ('FILLED','PARTIALLY_FILLED')
                            THEN o.filledsize ELSE 0 END) AS buyquantity
            FROM orders o
            WHERE o.userid = @UserId
            GROUP BY o.instrumentid
        )
        SELECT a.instrumentid          AS InstrumentId,
               i.ticker                AS Ticker,
               i.name                  AS Name,
               a.quantity::int         AS Quantity,
               a.reserved::int         AS Reserved,
               a.buycost               AS BuyCost,
               a.buyquantity::int      AS BuyQuantity,
               md."close"              AS Close,
               md.previousclose        AS PreviousClose
        FROM agg a
        JOIN instruments i ON i.id = a.instrumentid
        LEFT JOIN LATERAL (
            SELECT m."close", m.previousclose
            FROM marketdata m
            WHERE m.instrumentid = a.instrumentid
            ORDER BY m."date" DESC
            LIMIT 1
        ) md ON true
        WHERE i.type <> 'MONEDA'
          AND (a.quantity <> 0 OR a.reserved <> 0)
        ORDER BY i.ticker;
        """;

    public static async Task<Result<PortfolioResponse>> Handle(
        GetPortfolioQuery query,
        IDbConnectionFactory connectionFactory,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var userExists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM users WHERE id = @UserId);",
            new { query.UserId },
            cancellationToken: cancellationToken));

        if (!userExists)
            return Error.NotFound("user.not_found", $"No existe el usuario {query.UserId}.");

        var cash = await connection.QuerySingleAsync<CashRow>(new CommandDefinition(
            CashSql, new { query.UserId }, cancellationToken: cancellationToken));

        var rows = await connection.QueryAsync<PositionRow>(new CommandDefinition(
            PositionsSql, new { query.UserId }, cancellationToken: cancellationToken));

        var positions = rows.Select(ToPosition).ToList();

        // El valor total usa el cash CONTABLE, no el disponible: las reservas no salieron
        // de la cuenta, solo estan comprometidas. Descontarlas aca contaria de menos.
        var totalValue = cash.Accounting + positions.Sum(p => p.MarketValue ?? 0m);

        return new PortfolioResponse(
            query.UserId,
            TotalAccountValue: totalValue,
            AvailableCash: cash.Accounting - cash.Reserved,
            AccountingCash: cash.Accounting,
            ReservedCash: cash.Reserved,
            Positions: positions);
    }

    private static PositionResponse ToPosition(PositionRow row)
    {
        var averageCost = OrderMath.AverageCost(row.BuyCost, row.BuyQuantity);

        return new PositionResponse(
            row.InstrumentId,
            row.Ticker,
            row.Name,
            row.Quantity,
            AvailableQuantity: row.Quantity - row.Reserved,
            row.Close,
            MarketValue: row.Close is null ? null : row.Quantity * row.Close.Value,
            AverageCost: averageCost,
            TotalReturnPercent: OrderMath.TotalReturnPercent(row.Close, averageCost),
            DailyReturnPercent: OrderMath.DailyReturnPercent(row.Close, row.PreviousClose));
    }

    private sealed record CashRow(decimal Accounting, decimal Reserved);

    private sealed record PositionRow(
        int InstrumentId,
        string Ticker,
        string Name,
        int Quantity,
        int Reserved,
        decimal BuyCost,
        int BuyQuantity,
        decimal? Close,
        decimal? PreviousClose);
}
