using Cocos.Domain.Enums;

namespace Cocos.Domain;

/// <summary>
/// El motivo que acompana a una orden rechazada. Va junto al 201: quedarse sin fondos no es un
/// error del request sino un resultado de negocio, y el cliente necesita saber que lo evaluado
/// fue el disponible y no el saldo contable.
/// </summary>
public static class RejectionReason
{
    public static string For(OrderSide side) => side == OrderSide.Buy
        ? "Pesos disponibles insuficientes. El disponible descuenta lo reservado por órdenes de compra vivas."
        : "Acciones disponibles insuficientes. El disponible descuenta lo reservado por órdenes de venta vivas.";
}
