using Cocos.Application.Common;
using Cocos.Application.Features.Orders.CancelOrder;
using Cocos.Domain;
using Cocos.Domain.Entities;
using Cocos.Domain.Enums;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace Cocos.UnitTests;

/// <summary>
/// El caso de uso completo, sin base de datos. Es posible porque el libro de órdenes es un
/// objeto detras de una interfaz: antes el handler armaba el SQL el mismo y estos caminos
/// solo se podian ejercitar por HTTP contra un Postgres real.
/// </summary>
public class CancelOrderHandlerTests
{
    private static readonly DateTime Now = new(2023, 7, 14, 12, 0, 0, DateTimeKind.Utc);

    private const int Pamp = 47;
    private const int UserId = 1;

    private readonly FakeOrderBook _orders = new();

    private Task<Result<CancelOrderResponse>> Cancel(int orderId = 10)
        => CancelOrderHandler.Handle(
            new CancelOrderCommand(orderId, UserId), _orders, new FakeTimeProvider(Now), default);

    private static Order Viva() =>
        Order.Open(UserId, Pamp, OrderSide.Buy, size: 10, price: 900m, Now, OrderMath.EndOfDay(Now));

    private static Order Ejecutada() =>
        Order.Executed(UserId, Pamp, OrderSide.Buy, size: 10, price: 900m, Now);

    [Fact]
    public async Task Una_orden_inexistente_devuelve_404()
    {
        _orders.Order = null;

        var result = await Cancel();

        result.Error.Code.Should().Be("order.not_found");
        result.Error.Type.Should().Be(ErrorType.NotFound);
        _orders.Applied.Should().BeNull("no hay nada que cancelar");
    }

    [Fact]
    public async Task Una_orden_ya_ejecutada_no_se_puede_cancelar()
    {
        _orders.Order = Ejecutada();

        var result = await Cancel();

        result.Error.Code.Should().Be("order.not_cancellable");
        result.Error.Type.Should().Be(ErrorType.Conflict);
        result.Error.Message.Should().Contain("FILLED", "el motivo informa el estado real");
        _orders.Applied.Should().BeNull("una ejecucion es un hecho consumado: ni se intenta");
    }

    [Fact]
    public async Task Cancelar_una_orden_viva_registra_el_hecho_y_lo_informa()
    {
        _orders.Order = Viva();

        var result = await Cancel();

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be("CANCELLED");
        result.Value.CancelledAt.Should().Be(Now, "el instante lo pone el TimeProvider, no la base");
        _orders.Applied.Should().NotBeNull();
        _orders.Applied!.UserId.Should().Be(UserId, "la cancelación viaja con el dueno de la orden");
    }

    [Fact]
    public async Task Si_la_orden_deja_de_estar_viva_mientras_se_cancela_se_pierde_la_carrera()
    {
        _orders.Order = Viva();
        _orders.StillOpen = false;

        var result = await Cancel();

        result.Error.Code.Should().Be("order.no_longer_open");
        result.Error.Type.Should().Be(ErrorType.Conflict,
            "la vencio el job o la cancelo otra pestana entre la lectura y el registro");
    }

    // --- Fakes -------------------------------------------------------------------------

    private sealed class FakeOrderBook : IOrderBook
    {
        public Order? Order { get; set; }

        /// <summary>Simula el WHERE condicional: false es "la fila ya no estaba viva".</summary>
        public bool StillOpen { get; set; } = true;

        public OrderCancellation? Applied { get; private set; }

        public Task<Order?> FindAsync(int orderId, int userId, CancellationToken ct)
            => Task.FromResult(Order);

        public Task<bool> ApplyAsync(OrderCancellation cancellation)
        {
            Applied = cancellation;
            return Task.FromResult(StillOpen);
        }
    }
}
