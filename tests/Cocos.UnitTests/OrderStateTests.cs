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

    [Fact]
    public void Cancelar_una_orden_viva_produce_el_hecho_con_el_instante_recibido()
    {
        var order = Order.Open(1, 45, OrderSide.Buy, size: 50, price: 710m, Now, OrderMath.EndOfDay(Now));

        var cancellation = order.Cancel(Now);

        cancellation.Status.Should().Be(OrderStatus.Cancelled);
        cancellation.UserId.Should().Be(1);
        cancellation.CancelledAt.Should().Be(Now);
        order.Status.Should().Be(OrderStatus.New,
            "cancelar produce el hecho, no muta la orden: mutarla seria un read-modify-write");
    }

    [Fact]
    public void Cancelar_una_orden_terminal_es_un_error_de_programacion_y_no_un_resultado()
    {
        var order = Order.Executed(1, 47, OrderSide.Buy, size: 50, price: 930m, Now);

        var cancelar = () => order.Cancel(Now);

        cancelar.Should().Throw<InvalidOperationException>(
            "el caso de uso pregunta por CanBeCancelled antes; llegar aca es un bug, no un 409");
    }

    [Fact]
    public void Una_orden_viva_vence_recien_cuando_su_jornada_termina()
    {
        var order = Order.Open(1, 45, OrderSide.Buy, size: 50, price: 710m, Now, OrderMath.EndOfDay(Now));
        var cierre = OrderMath.EndOfDay(Now);

        order.HasExpired(Now).Should().BeFalse("la jornada todavia no termino");
        order.HasExpired(cierre.AddTicks(-1)).Should().BeFalse();
        order.HasExpired(cierre).Should().BeTrue("el limite es inclusivo");
        order.HasExpired(cierre.AddDays(1)).Should().BeTrue();
    }

    [Fact]
    public void Una_MARKET_no_vence_nunca_porque_nace_sin_vencimiento()
    {
        var order = Order.Executed(1, 47, OrderSide.Buy, size: 50, price: 930m, Now);

        order.ExpiresAt.Should().BeNull();
        order.HasExpired(Now.AddYears(10)).Should().BeFalse(
            "el criterio la descarta sola, sin preguntar por el tipo");
    }

    [Fact]
    public void Una_orden_que_ya_no_esta_viva_no_puede_vencer()
    {
        // Una rechazada no reserva nada: vencerla no liberaria nada y ensuciaria su estado
        // terminal, que es el registro de lo que efectivamente paso.
        var order = Order.Rejected(1, 47, OrderSide.Sell, OrderType.Market, size: 100, price: 950m, Now);

        order.HasExpired(Now.AddYears(10)).Should().BeFalse();
    }

    [Fact]
    public void Los_estados_vivos_que_viajan_al_SQL_son_exactamente_los_que_el_dominio_considera_vivos()
    {
        // Costura entre la regla del dominio (Order.IsOpen, ya fijada arriba contra entidades
        // reales) y la lista que viaja por parámetro a los dos UPDATE condicionales: el de la
        // cancelación y el del barrido de vencimiento. Sin este test, agregar un estado vivo
        // compila, pasa los tests de dominio, y las dos escrituras dejan de encontrar sus
        // filas en silencio.
        var vivos = Enum.GetValues<OrderStatus>()
            .Where(status => status is OrderStatus.New or OrderStatus.PartiallyFilled)
            .Select(status => status.ToDb());

        vivos.Should().BeEquivalentTo(DbValues.OpenStatuses);
    }
}
