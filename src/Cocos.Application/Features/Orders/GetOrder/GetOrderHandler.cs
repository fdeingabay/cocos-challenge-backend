using Cocos.Application.Common;
using Dapper;

namespace Cocos.Application.Features.Orders.GetOrder;

/// <summary>
/// Lectura de una orden puntual, dentro del alcance del usuario que pregunta.
///
/// El userId es obligatorio en el endpoint: sin el se bindearia a 0 y la respuesta seria un 404
/// que dice que la orden no existe cuando en realidad existe. Falta un parámetro: eso es 400.
/// </summary>
public static class GetOrderHandler
{
    // El notional se calcula en SQL y no en C#, como toda lectura del repo: la proyeccion baja
    // directo al record inmutable, sin objeto intermedio.
    private const string Sql =
        """
        SELECT o.id           AS Id,
               o.userid       AS UserId,
               o.instrumentid AS InstrumentId,
               i.ticker       AS Ticker,
               o.side         AS Side,
               o.type         AS Type,
               o.status       AS Status,
               o.size         AS Size,
               o.filledsize   AS FilledSize,
               o.price        AS Price,
               o.size * o.price AS Notional,
               o.datetime     AS DateTime,
               o.expiresat    AS ExpiresAt,
               o.cancelledat  AS CancelledAt
        FROM orders o
        JOIN instruments i ON i.id = o.instrumentid
        WHERE o.id = @OrderId AND o.userid = @UserId;
        """;

    public static async Task<Result<GetOrderResponse>> Handle(
        GetOrderQuery query,
        IDbConnectionFactory connectionFactory,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        var order = await connection.QuerySingleOrDefaultAsync<GetOrderResponse>(new CommandDefinition(
            Sql, new { query.OrderId, query.UserId }, cancellationToken: cancellationToken));

        // El userid va en el WHERE y no en un chequeo posterior: una orden ajena responde igual
        // que una inexistente. Contestar distinto le confirmaria que esa orden existe para otro.
        return order is null
            ? Error.NotFound("order.not_found",
                $"No existe la orden {query.OrderId} para el usuario {query.UserId}.")
            : order;
    }
}
