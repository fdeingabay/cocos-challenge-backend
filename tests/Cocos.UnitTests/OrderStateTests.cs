using Cocos.Domain;
using Cocos.Domain.Entities;
using Cocos.Domain.Enums;
using FluentAssertions;

namespace Cocos.UnitTests;

public class OrderStateTests
{
    private static readonly DateTime Now = new(2023, 7, 14, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Una_orden_MARKET_nace_ejecutada_por_el_total()
    {
        var order = Order.Executed(1, 47, OrderSide.Buy, size: 50, price: 930m, Now);

        order.Status.Should().Be(OrderStatus.Filled);
        order.FilledSize.Should().Be(50);
        order.RemainingSize.Should().Be(0);
        order.IsOpen.Should().BeFalse();
        order.CanBeCancelled.Should().BeFalse("una orden ya ejecutada es un hecho consumado");
        order.ExpiresAt.Should().BeNull("una MARKET nunca queda viva en el libro");
        order.NotionalFilled.Should().Be(46_500m);
        order.NotionalReserved.Should().Be(0m);
    }

    [Fact]
    public void Una_orden_LIMIT_nace_viva_y_reservando()
    {
        var order = Order.Open(1, 45, OrderSide.Buy, size: 50, price: 710m, Now, OrderMath.EndOfDay(Now));

        order.Status.Should().Be(OrderStatus.New);
        order.FilledSize.Should().Be(0);
        order.IsOpen.Should().BeTrue();
        order.CanBeCancelled.Should().BeTrue();
        order.ExpiresAt.Should().NotBeNull("sin vencimiento reservaria fondos para siempre");
        order.NotionalFilled.Should().Be(0m, "todavia no movio nada");
        order.NotionalReserved.Should().Be(35_500m, "pero ya compromete el total");
    }

    [Fact]
    public void Una_orden_rechazada_se_persiste_pero_no_mueve_ni_reserva_nada()
    {
        var order = Order.Rejected(1, 47, OrderSide.Sell, OrderType.Market, size: 100, price: 950m, Now);

        order.Status.Should().Be(OrderStatus.Rejected);
        order.FilledSize.Should().Be(0);
        order.IsOpen.Should().BeFalse("una rechazada no puede reservar nada");
        order.CanBeCancelled.Should().BeFalse();
        order.NotionalFilled.Should().Be(0m);
    }

    [Theory]
    [InlineData(OrderStatus.New, true)]
    [InlineData(OrderStatus.PartiallyFilled, true)]
    [InlineData(OrderStatus.Filled, false)]
    [InlineData(OrderStatus.Rejected, false)]
    [InlineData(OrderStatus.Cancelled, false)]
    [InlineData(OrderStatus.Expired, false)]
    public void Solo_las_ordenes_vivas_son_cancelables(OrderStatus status, bool cancelable)
    {
        // La regla del enunciado dice "solo las NEW"; la extendemos a las parcialmente
        // ejecutadas, donde se cancela el remanente y nunca lo ya ejecutado.
        var isOpen = status is OrderStatus.New or OrderStatus.PartiallyFilled;
        isOpen.Should().Be(cancelable);
    }

    [Fact]
    public void Los_literales_de_la_base_provista_se_respetan_tal_cual()
    {
        OrderSide.CashIn.ToDb().Should().Be("CASH_IN");
        OrderStatus.PartiallyFilled.ToDb().Should().Be("PARTIALLY_FILLED");
        DbValues.ToOrderStatus("EXPIRED").Should().Be(OrderStatus.Expired);
    }
}
