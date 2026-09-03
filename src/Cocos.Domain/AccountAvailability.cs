using Cocos.Domain.Enums;

namespace Cocos.Domain;

/// <summary>
/// Cuanto puede comprometer la cuenta ahora mismo. No es el saldo contable: ya viene neto de
/// reserva, o sea descontado lo que retienen las órdenes vivas. Sin ese descuento el mismo peso
/// respalda dos órdenes y la cuenta queda sobregirada.
///
/// Se arma por lado porque cada orden consume un solo recurso: la compra consume poder de
/// compra y la venta consume nominales. Asi el ledger corre una sola agregacion con el lock
/// de cuenta tomado.
/// </summary>
public readonly record struct AccountAvailability
{
    private AccountAvailability(decimal cash, int quantity)
    {
        Cash = cash;
        Quantity = quantity;
    }

    /// <summary>Poder de compra: pesos libres de reserva. Solo aplica a la compra.</summary>
    public decimal Cash { get; }

    /// <summary>Nominales del instrumento libres de reserva. Solo aplica a la venta.</summary>
    public int Quantity { get; }

    public static AccountAvailability ForBuy(decimal cash) => new(cash, 0);

    public static AccountAvailability ForSell(int quantity) => new(0m, quantity);

    /// <summary>
    /// Si la cuenta cubre la orden. El limite es inclusivo: comprometer todo el disponible vale,
    /// excederlo no.
    /// </summary>
    public bool CanSupport(OrderRequest request) => request.Side switch
    {
        OrderSide.Buy => Cash >= request.Notional,
        OrderSide.Sell => Quantity >= request.Size,
        // CASH_IN / CASH_OUT son movimientos de fondos, no órdenes al mercado: no pasan por aca.
        _ => false
    };
}
