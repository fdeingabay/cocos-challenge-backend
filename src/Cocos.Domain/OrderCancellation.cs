using Cocos.Domain.Enums;

namespace Cocos.Domain;

/// <summary>
/// La cancelación de una orden viva: el hecho ya decidido y todavia no registrado.
///
/// Es un objeto y no un "Status = Cancelled" porque la decision vale mientras la orden siga
/// viva. Quien la registre tiene que verificar esa condicion en el mismo acto de escribirla:
/// verificarla antes deja una ventana en la que dos cancelaciones liberan la reserva dos veces.
/// </summary>
public sealed record OrderCancellation(int OrderId, int UserId, DateTime CancelledAt)
{
    /// <summary>Estado en el que queda la orden. Terminal: libera la reserva.</summary>
    public OrderStatus Status => OrderStatus.Cancelled;
}
