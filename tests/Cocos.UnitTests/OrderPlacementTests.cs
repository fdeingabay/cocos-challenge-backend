using Cocos.Domain;
using Cocos.Domain.Entities;
using Cocos.Domain.Enums;
using FluentAssertions;

namespace Cocos.UnitTests;

/// <summary>
/// La decision central del envio de órdenes: en que estado nace. Antes vivia en un if/else
/// del handler y solo se podia probar levantando un Postgres.
/// </summary>
public class OrderPlacementTests
{
    private static readonly DateTime Now = new(2023, 7, 14, 12, 0, 0, DateTimeKind.Utc);

    private static OrderRequest Request(OrderType type) => OrderRequest.For(
        userId: 1, instrumentId: 47, OrderSide.Buy, type,
        size: 10, amount: null, price: 100m, idempotencyKey: "k-1");

    [Fact]
    public void Con_disponible_una_MARKET_nace_ejecutada()
    {
        var order = Order.Place(Request(OrderType.Market), hasFunds: true, Now);

        order.Status.Should().Be(OrderStatus.Filled);
        order.FilledSize.Should().Be(10);
        order.ExpiresAt.Should().BeNull();
    }

    [Fact]
    public void Con_disponible_una_LIMIT_nace_viva_y_vence_al_cierre_de_la_jornada()
    {
        var order = Order.Place(Request(OrderType.Limit), hasFunds: true, Now);

        order.Status.Should().Be(OrderStatus.New);
        order.FilledSize.Should().Be(0);
        order.NotionalReserved.Should().Be(1_000m);
        order.ExpiresAt.Should().Be(OrderMath.EndOfDay(Now));
    }

    [Theory]
    [InlineData(OrderType.Market)]
    [InlineData(OrderType.Limit)]
    public void Sin_disponible_se_rechaza_cualquiera_sea_el_tipo(OrderType type)
    {
        var order = Order.Place(Request(type), hasFunds: false, Now);

        order.Status.Should().Be(OrderStatus.Rejected);
        order.Type.Should().Be(type, "el rechazo no cambia el tipo de orden que se pidio");
        order.FilledSize.Should().Be(0);
        order.IsOpen.Should().BeFalse("una rechazada no puede reservar nada");
        order.NotionalFilled.Should().Be(0m);
        order.ExpiresAt.Should().BeNull();
    }

    [Fact]
    public void La_clave_de_idempotencia_viaja_a_la_orden_en_los_tres_casos()
    {
        Order.Place(Request(OrderType.Market), hasFunds: true, Now).IdempotencyKey.Should().Be("k-1");
        Order.Place(Request(OrderType.Limit), hasFunds: true, Now).IdempotencyKey.Should().Be("k-1");
        Order.Place(Request(OrderType.Market), hasFunds: false, Now).IdempotencyKey.Should().Be("k-1");
    }
}
