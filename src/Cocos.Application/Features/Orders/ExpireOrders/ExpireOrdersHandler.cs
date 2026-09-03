using Cocos.Domain;

namespace Cocos.Application.Features.Orders.ExpireOrders;

/// <summary>
/// Sin vencimiento una orden LIMIT vive para siempre reservando fondos que el usuario nunca
/// recupera. Las órdenes son DAY: vencen al cierre de la jornada en que se enviaron.
///
/// Es el unico handler que no devuelve Result&lt;T&gt;, y no es un descuido: no hay cliente HTTP
/// del otro lado al que mapearle un error. Lo dispara OrderExpirationService, que trata
/// cualquier fallo como excepcion y reintenta en el proximo tick.
///
/// Tampoco recibe CancellationToken: con el criterio ya formado no queda nada a lo que
/// pasarselo, y la firma dice sola que este caso de uso no se interrumpe por la mitad. El
/// apagado del host lo maneja el servicio, un nivel mas arriba.
/// </summary>
public static class ExpireOrdersHandler
{
    public static async Task<ExpireOrdersResponse> Handle(
        ExpireOrdersCommand command,
        IOpenOrders orders,
        TimeProvider timeProvider)
    {
        var expiry = OrderExpiry.AsOfNow(timeProvider);

        return new ExpireOrdersResponse(await orders.ApplyAsync(expiry));
    }
}
