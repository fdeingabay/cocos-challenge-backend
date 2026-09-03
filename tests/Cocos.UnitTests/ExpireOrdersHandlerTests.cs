using Cocos.Application.Features.Orders.ExpireOrders;
using Cocos.Domain;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace Cocos.UnitTests;

/// <summary>
/// Primera cobertura del barrido. Antes el caso de uso solo se podia ejercitar esperando a que
/// el PeriodicTimer disparara contra un Postgres real: el handler armaba el SQL el mismo y no
/// habia forma de observarlo sin base.
/// </summary>
public class ExpireOrdersHandlerTests
{
    private static readonly DateTime Now = new(2023, 7, 14, 12, 0, 0, DateTimeKind.Utc);

    private readonly FakeOpenOrders _orders = new();

    private Task<ExpireOrdersResponse> Barrer()
        => ExpireOrdersHandler.Handle(new ExpireOrdersCommand(), _orders, new FakeTimeProvider(Now));

    [Fact]
    public async Task El_criterio_se_arma_con_el_reloj_inyectado_y_no_con_el_del_sistema()
    {
        await Barrer();

        _orders.Applied.Should().NotBeNull();
        _orders.Applied!.AsOf.Should().Be(Now,
            "sin TimeProvider el vencimiento solo se puede testear esperando a que pase el dia");
    }

    [Fact]
    public async Task Las_ordenes_barridas_quedan_vencidas_y_no_canceladas()
    {
        await Barrer();

        _orders.Applied!.Status.Should().Be(Cocos.Domain.Enums.OrderStatus.Expired,
            "vencer y cancelar liberan la reserva igual, pero no son el mismo hecho");
    }

    [Fact]
    public async Task Se_informa_cuantas_ordenes_vencieron()
    {
        _orders.Expired = 3;

        var response = await Barrer();

        response.ExpiredCount.Should().Be(3);
    }

    [Fact]
    public async Task Un_barrido_sin_nada_que_vencer_no_es_un_error()
    {
        _orders.Expired = 0;

        var response = await Barrer();

        response.ExpiredCount.Should().Be(0, "el job corre cada pocos minutos: lo normal es que no haya nada");
    }

    // --- Fakes -------------------------------------------------------------------------

    private sealed class FakeOpenOrders : IOpenOrders
    {
        public int Expired { get; set; }
        public OrderExpiry? Applied { get; private set; }

        public Task<int> ApplyAsync(OrderExpiry expiry)
        {
            Applied = expiry;
            return Task.FromResult(Expired);
        }
    }
}
