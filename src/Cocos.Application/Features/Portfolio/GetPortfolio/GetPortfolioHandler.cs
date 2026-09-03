using Cocos.Application.Common;
using Cocos.Domain;
using Cocos.Domain.Entities;
using Dapper;

namespace Cocos.Application.Features.Portfolio.GetPortfolio;

public static class GetPortfolioHandler
{
    // El cash es un escalar sobre todas las órdenes; las posiciones son un agregado por
    // instrumento. Van en dos statements para evitar el CASE anidado que haria falta para sacar
    // los dos en una pasada.
    //
    // Las expresiones salen de LedgerSql: son LAS MISMAS con las que el envio de órdenes decide
    // si la cuenta cubre un pedido. Informar el disponible con una cuenta y decidir con otra
    // rompe el invariante en silencio.
    private const string CashSql =
        $"""
         SELECT {LedgerSql.AccountingCash} AS Accounting,
                {LedgerSql.ReservedCash}   AS Reserved
         FROM orders
         WHERE userid = @UserId;
         """;

    // El LEFT JOIN LATERAL trae UNA fila de marketdata por instrumento: la mas reciente. Un JOIN
    // comun duplicaria cada posicion (hay 2 dias cargados por instrumento), y una consulta por
    // posicion seria el N+1 clasico.
    //
    // La tabla va SIN alias en el CTE: los fragmentos de LedgerSql agregan sobre columnas sin
    // calificar, que es lo que les permite servir a las cuatro consultas del invariante.
    //
    // El PPP necesita un recorrido ORDENADO y no una agregacion, y esa es la parte cara de esta
    // consulta. Promediar todas las compras de la historia no es el PPP: coincide con el solo
    // mientras no haya ventas. Al cerrar una posicion y volver a abrirla, ese promedio arrastra
    // compras de una tenencia que ya no existe e informa un costo que nadie pago.
    //
    // La regla del PPP es que la venta reduce el costo total en la misma proporcion en que reduce
    // la tenencia, dejando el promedio intacto. Eso es multiplicativo y secuencial: no hay forma
    // cerrada, hay que caminar los movimientos en orden. De ahi el CTE recursivo.
    private const string PositionsSql =
        $"""
        WITH RECURSIVE agg AS (
            SELECT instrumentid,
                   {LedgerSql.ExecutedQuantity} AS quantity,
                   {LedgerSql.ReservedQuantity} AS reserved
            FROM orders
            WHERE userid = @UserId
            GROUP BY instrumentid
        ),
        -- Solo lo que movio tenencia, en orden cronologico. El filtro por filledsize deja afuera
        -- las NEW y las REJECTED sin nombrar ningun estado: si no ejecuto nada, no participa del
        -- costeo. El id desempata las que comparten datetime.
        movimientos AS (
            SELECT instrumentid, side, filledsize, price,
                   ROW_NUMBER() OVER (PARTITION BY instrumentid ORDER BY datetime, id) AS paso
            FROM orders
            WHERE userid = @UserId AND filledsize > 0 AND side IN ('BUY', 'SELL')
        ),
        costeo AS (
            SELECT instrumentid, paso,
                   (CASE WHEN side = 'BUY' THEN filledsize ELSE -filledsize END)::numeric AS tenencia,
                   (CASE WHEN side = 'BUY' THEN filledsize * price ELSE 0 END)            AS costo
            FROM movimientos
            WHERE paso = 1
            UNION ALL
            SELECT m.instrumentid, m.paso,
                   c.tenencia + CASE WHEN m.side = 'BUY' THEN m.filledsize ELSE -m.filledsize END,
                   CASE
                       -- Comprar suma lo que se pago.
                       WHEN m.side = 'BUY' THEN c.costo + m.filledsize * m.price
                       -- Vender achica el costo en la misma proporcion que la tenencia, asi que
                       -- el promedio no se mueve. Al cerrar la posicion el costo queda en cero.
                       WHEN c.tenencia > 0
                           THEN c.costo * GREATEST(c.tenencia - m.filledsize, 0) / c.tenencia
                       ELSE 0
                   END
            FROM costeo c
            JOIN movimientos m ON m.instrumentid = c.instrumentid AND m.paso = c.paso + 1
        ),
        -- El último paso de cada instrumento es el costeo al dia de hoy.
        ppp AS (
            SELECT DISTINCT ON (instrumentid) instrumentid, costo
            FROM costeo
            ORDER BY instrumentid, paso DESC
        )
        SELECT a.instrumentid          AS InstrumentId,
               i.ticker                AS Ticker,
               i.name                  AS Name,
               a.quantity::int         AS Quantity,
               a.reserved::int         AS Reserved,
               COALESCE(p.costo, 0)    AS CostBasis,
               md."close"              AS Close,
               md.previousclose        AS PreviousClose
        FROM agg a
        JOIN instruments i ON i.id = a.instrumentid
        LEFT JOIN ppp p ON p.instrumentid = a.instrumentid
        LEFT JOIN LATERAL (
            SELECT m."close", m.previousclose
            FROM marketdata m
            WHERE m.instrumentid = a.instrumentid
            ORDER BY m."date" DESC
            LIMIT 1
        ) md ON true
        WHERE i.type <> @CurrencyType
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

        // Los estados vivos los define el dominio (DbValues) y viajan por parámetro: las
        // expresiones de LedgerSql no llevan reglas de negocio escritas adentro.
        var parameters = new
        {
            query.UserId,
            DbValues.OpenStatuses,
            // El ARS es un instrumento MONEDA y no es una posicion: se informa como
            // availableCash. Cual es el literal lo sabe el dominio, no esta consulta.
            CurrencyType = Instrument.CurrencyType
        };

        var cash = await connection.QuerySingleAsync<CashRow>(new CommandDefinition(
            CashSql, parameters, cancellationToken: cancellationToken));

        var rows = await connection.QueryAsync<PositionRow>(new CommandDefinition(
            PositionsSql, parameters, cancellationToken: cancellationToken));

        var positions = rows.Select(ToPosition).ToList();

        // El valor total usa el cash CONTABLE y no el disponible: la plata reservada no salio de
        // la cuenta, solo esta comprometida. Descontarla aca valuaria la cuenta de menos.
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
        var averageCost = OrderMath.AverageCost(row.CostBasis, row.Quantity);

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
        decimal CostBasis,
        decimal? Close,
        decimal? PreviousClose);
}
