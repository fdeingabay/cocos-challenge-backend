using Cocos.Domain.Entities;

namespace Cocos.Domain;

/// <summary>
/// El motivo por el que una orden no se puede cancelar. Es el texto del 409.
/// </summary>
public static class CancellationRefusal
{
    public static string For(Order order) =>
        $"La orden {order.Id} esta en estado {order.Status.ToDb()} y solo se pueden cancelar " +
        "las órdenes vivas (NEW o PARTIALLY_FILLED).";
}
