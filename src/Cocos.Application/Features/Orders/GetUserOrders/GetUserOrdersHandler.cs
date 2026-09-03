using Cocos.Application.Common;
using Cocos.Domain;
using Dapper;

namespace Cocos.Application.Features.Orders.GetUserOrders;

/// <summary>
/// Las órdenes de un usuario, paginadas y opcionalmente filtradas por estado.
///
/// Un estado que no existe es un 400 y no una lista vacia: "no hay órdenes en ese estado" y "ese
/// estado no existe" son respuestas distintas, y confundirlas hace pasar un error del cliente por
/// un resultado. El filtro se convierte en dominio (OrderStatusFilter) antes de tocar la base.
/// </summary>
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
        if (!OrderStatusFilter.TryParse(query.Status, out var status))
            return Error.Validation("order.unknown_status", OrderStatusFilter.Unknown(query.Status));

        var page = Paging.NormalizePage(query.Page);
        var pageSize = Paging.NormalizePageSize(query.PageSize);

        var parameters = new
        {
            query.UserId,
            Status = status?.ToDb(),
            Take = pageSize,
            Skip = (page - 1) * pageSize
        };

        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        // "No existe el usuario" y "el usuario no tiene órdenes" son respuestas distintas:
        // devolver una lista vacia para la primera esconde un error del cliente. La consulta se
        // repite en los dos slices que la necesitan a propósito: son dos lineas, y compartirlas
        // acoplaria dos casos de uso que si no no se conocen.
        var userExists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT EXISTS (SELECT 1 FROM users WHERE id = @UserId);",
            parameters, cancellationToken: cancellationToken));

        if (!userExists)
            return Error.NotFound("user.not_found", $"No existe el usuario {query.UserId}.");

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
