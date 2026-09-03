using Cocos.Domain.Enums;

namespace Cocos.Domain;

/// <summary>
/// El vencimiento evaluado a un instante: toda orden que siga viva con su jornada ya terminada
/// deja de estarlo.
///
/// Como en <see cref="OrderCancellation"/>, el criterio vale para ESE instante y hay que
/// evaluarlo al escribir. A diferencia de una cancelación no apunta a una orden: vencer no es
/// una decision por entidad sino un criterio que se aplica en conjunto, y cargar las órdenes
/// una por una para vencerlas seria un N+1.
/// </summary>
public sealed record OrderExpiry(DateTime AsOf)
{
    /// <summary>Estado en el que quedan las órdenes barridas. Terminal: libera la reserva.</summary>
    public OrderStatus Status => OrderStatus.Expired;

    /// <summary>
    /// El criterio al instante actual. El reloj entra por TimeProvider: si no, vencer solo se
    /// podria testear esperando a que pase el dia.
    /// </summary>
    public static OrderExpiry AsOfNow(TimeProvider timeProvider) =>
        new(timeProvider.GetUtcNow().UtcDateTime);
}
