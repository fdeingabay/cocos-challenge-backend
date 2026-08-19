using Cocos.Application.Common;
using Dapper;

namespace Cocos.Application.Features.Orders.CancelOrder;

/// <summary>
/// Cancelacion de una orden viva. Libera implicitamente la reserva: como el disponible se
/// calcula sobre las ordenes en estado vivo, al dejar de estarlo la orden deja de restar.
/// </summary>
public static class CancelOrderHandler
{
    // UPDATE condicional: la condicion de estado viaja en el WHERE, no se evalua antes en
    // memoria. Dos cancelaciones simultaneas -- o una cancelacion compitiendo con el job de
    // expiracion -- hacen que solo una afecte filas; la otra ve 0 y sabe que perdio la
    // carrera. Sin esto la reserva se podria liberar dos veces.
    private const string CancelSql =
        """
        UPDATE orders
           SET status = 'CANCELLED'
         WHERE id = @OrderId
           AND userid = @UserId
           AND status IN ('NEW','PARTIALLY_FILLED');
        """;

    private const string CurrentStatusSql =
        "SELECT status FROM orders WHERE id = @OrderId AND userid = @UserId;";

    public static async Task<Result<CancelOrderResponse>> Handle(
        CancelOrderCommand command,
        IDbConnectionFactory connectionFactory,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        // Un unico statement condicional ya es atomico: no hace falta transaccion explicita.
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            CancelSql, new { command.OrderId, command.UserId },
            cancellationToken: cancellationToken));

        if (affected == 1)
            return Result.Success(new CancelOrderResponse(
                command.OrderId, "CANCELLED", timeProvider.GetUtcNow().UtcDateTime));

        // No se afecto ninguna fila: o la orden no es de este usuario / no existe,
        // o ya no estaba en un estado cancelable.
        var currentStatus = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            CurrentStatusSql, new { command.OrderId, command.UserId },
            cancellationToken: cancellationToken));

        return currentStatus is null
            ? Error.NotFound("order.not_found",
                $"No existe la orden {command.OrderId} para el usuario {command.UserId}.")
            : Error.Conflict("order.not_cancellable",
                $"La orden {command.OrderId} esta en estado {currentStatus} y solo se pueden cancelar las ordenes vivas (NEW o PARTIALLY_FILLED).");
    }
}
