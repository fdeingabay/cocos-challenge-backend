using Cocos.Application.Common;
using Dapper;

namespace Cocos.Application.Features.Orders.ExpireOrders;

/// <summary>
/// Sin expiracion una orden LIMIT vive para siempre reservando fondos que el usuario nunca
/// recupera. Las ordenes son DAY: vencen al cierre de la jornada en que se enviaron.
/// </summary>
public static class ExpireOrdersHandler
{
    // Un unico UPDATE masivo condicional. Es idempotente por construccion: el filtro por
    // status hace que una segunda corrida afecte 0 filas. Por eso puede correr en N
    // instancias sin leader election ni claim -- la primera gana y las demas no hacen nada.
    private const string ExpireSql =
        """
        UPDATE orders
           SET status = 'EXPIRED'
         WHERE status IN ('NEW','PARTIALLY_FILLED')
           AND expiresat IS NOT NULL
           AND expiresat <= @Now;
        """;

    public static async Task<ExpireOrdersResponse> Handle(
        ExpireOrdersCommand command,
        IDbConnectionFactory connectionFactory,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        // El "ahora" viene del TimeProvider, no de DateTime.UtcNow: sin eso el vencimiento
        // solo se puede testear esperando a que pase el dia.
        var now = timeProvider.GetUtcNow().UtcDateTime;

        // La apertura de la conexion si respeta la cancelacion: todavia no se empezo a escribir.
        await using var connection = await connectionFactory.OpenAsync(cancellationToken);

        // El UPDATE en cambio se ejecuta con None. Es un commit de datos: si el host se esta
        // apagando preferimos terminar este statement -- que es corto y atomico -- antes que
        // dejar el barrido a medio camino y tener que razonar sobre que quedo vencido y que no.
        var expired = await connection.ExecuteAsync(new CommandDefinition(
            ExpireSql, new { Now = now }, cancellationToken: CancellationToken.None));

        return new ExpireOrdersResponse(expired);
    }
}
