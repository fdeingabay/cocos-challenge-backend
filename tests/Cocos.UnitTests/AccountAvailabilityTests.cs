using Cocos.Domain;
using Cocos.Domain.Enums;
using FluentAssertions;

namespace Cocos.UnitTests;

/// <summary>
/// El disponible que se compara aca ya viene con la reserva descontada. Estos tests fijan
/// la comparacion, no el calculo del disponible (eso es SQL y se prueba por integracion).
/// </summary>
public class AccountAvailabilityTests
{
    private static OrderRequest Buy(int size, decimal price) => OrderRequest.For(
        userId: 1, instrumentId: 47, OrderSide.Buy, OrderType.Market,
        size, amount: null, price, idempotencyKey: null);

    private static OrderRequest Sell(int size, decimal price) => OrderRequest.For(
        userId: 1, instrumentId: 47, OrderSide.Sell, OrderType.Market,
        size, amount: null, price, idempotencyKey: null);

    [Fact]
    public void Una_compra_se_mide_contra_los_pesos_disponibles()
    {
        var request = Buy(size: 10, price: 100m);

        AccountAvailability.ForBuy(1_000m).CanSupport(request).Should().BeTrue();
        AccountAvailability.ForBuy(999.99m).CanSupport(request).Should().BeFalse();
    }

    [Fact]
    public void Gastar_hasta_el_ultimo_peso_disponible_es_valido()
    {
        // El limite es inclusivo: lo que no se puede es pasarse.
        var request = Buy(size: 3, price: 333.33m);

        AccountAvailability.ForBuy(999.99m).CanSupport(request).Should().BeTrue();
    }

    [Fact]
    public void Una_venta_se_mide_contra_los_nominales_disponibles()
    {
        var request = Sell(size: 50, price: 930m);

        AccountAvailability.ForSell(50).CanSupport(request).Should().BeTrue();
        AccountAvailability.ForSell(49).CanSupport(request).Should().BeFalse();
    }

    [Fact]
    public void Una_venta_no_mira_el_cash_ni_una_compra_los_nominales()
    {
        // Cada lado consume un solo recurso: por eso el ledger ejecuta una sola agregacion.
        AccountAvailability.ForSell(100).CanSupport(Buy(size: 1, price: 1m))
            .Should().BeFalse("una compra sin pesos no se salva por tener acciones");

        AccountAvailability.ForBuy(1_000_000m).CanSupport(Sell(size: 1, price: 1m))
            .Should().BeFalse("una venta sin tenencia no se salva por tener pesos");
    }
}
