using Cocos.Application.Common;
using Dapper;

namespace Cocos.Application.Features.Orders.GetUserOrders;

public static class GetUserOrdersHandler
{
    private const string WhereClause =
        """
        WHERE o.userid = @UserId
          AND (@Status IS NULL OR o.status = @Status)
        """;

    public static async Task<Result<PagedResult<OrderSummaryResponse>>> Handle(
        GetUserOrdersQuery query,
        IDbConnectionFactory connectionFactory,
        CancellationToken cancellationToken)
    {
        var page = Paging.NormalizePage(query.Page);
        var pageSize = Paging.NormalizePageSize(query.PageSize);

        var parameters = new
        {
            query.UserId,
            Status = string.IsNullOrWhiteSpace(query.Status) ? null : query.Status.Trim().ToUpperInvariant(),
            Take = pageSize,
            Skip = (page - 1) * pageSize
        };

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var total = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT COUNT(*) FROM orders o {WhereClause};",
            parameters, cancellationToken: cancellationToken));

        var items = await connection.QueryAsync<OrderSummaryResponse>(new CommandDefinition(
            $"""
             SELECT o.id            AS Id,
                    o.instrumentid  AS InstrumentId,
                    i.ticker        AS Ticker,
                    o.side          AS Side,
                    o.type          AS Type,
                    o.status        AS Status,
                    o.size          AS Size,
                    o.filledsize    AS FilledSize,
                    o.price         AS Price,
                    o.datetime      AS DateTime,
                    o.expiresat     AS ExpiresAt
             FROM orders o
             JOIN instruments i ON i.id = o.instrumentid
             {WhereClause}
             ORDER BY o.datetime DESC, o.id DESC
             LIMIT @Take OFFSET @Skip;
             """,
            parameters, cancellationToken: cancellationToken));

        return new PagedResult<OrderSummaryResponse>(items.ToList(), page, pageSize, total);
    }
}
