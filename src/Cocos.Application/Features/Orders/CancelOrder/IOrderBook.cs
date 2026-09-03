using Cocos.Domain;
using Cocos.Domain.Entities;

namespace Cocos.Application.Features.Orders.CancelOrder;

/// <summary>
/// El libro de órdenes, visto desde el caso de uso que cancela.
///
/// A diferencia del envio, cancelar no necesita el lock de cuenta: el conflicto vive en UNA fila
/// -- la orden -- y no en la suma de todas, y una fila la defiende Postgres solo. Por eso aca no
/// hay nada parecido a IAccountLock.
/// </summary>
public interface IOrderBook
{
    /// <summary>
    /// La orden, dentro del alcance del usuario que pregunta. Una orden ajena responde igual que
    /// una inexistente: contestar distinto le confirmaria que esa orden existe para otro.
    /// </summary>
    Task<Order?> FindAsync(int orderId, int userId, CancellationToken cancellationToken);

    /// <summary>
    /// Registra la cancelación, y solo si la orden sigue viva. Devuelve false si dejo de estarlo
    /// entre la decision y el registro: perdio la carrera contra otra cancelación, contra el
    /// vencimiento o contra una ejecucion. Devuelve un resultado y no lanza una excepcion porque
    /// perder esa carrera es un desenlace de negocio, no un fallo.
    ///
    /// No recibe CancellationToken: con la decision ya tomada, que el cliente corte la conexión
    /// no puede dejar la reserva retenida. La firma lo impide en vez de confiarlo a un comentario.
    /// </summary>
    Task<bool> ApplyAsync(OrderCancellation cancellation);
}
