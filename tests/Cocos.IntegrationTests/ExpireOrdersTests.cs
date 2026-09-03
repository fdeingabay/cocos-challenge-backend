using System.Net.Http.Json;
using Cocos.Application.Features.Orders.ExpireOrders;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Wolverine;

namespace Cocos.IntegrationTests;

/// <summary>
/// El barrido de vencimiento de punta a punta. Se dispara invocando el comando por el bus en
/// vez de esperar al PeriodicTimer: lo que se prueba es el caso de uso, no el temporizador.
///
/// Las órdenes se "envejecen" con un UPDATE directo sobre expiresat. Es la única forma de
/// simular que paso la jornada sin mover el reloj del host, y deja el resto del flujo intacto:
/// las órdenes se crean por HTTP, como en produccion.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ExpireOrdersTests(PostgresFixture fixture) : IntegrationTestBase(fixture)
{
    private const int UserId = 1;
    private const int Pamp = 47;

    [Fact]
    public async Task El_primer_barrido_vence_las_ordenes_NEW_del_seed()
    {
        // Las dos NEW provistas son de julio de 2023 y la migracion V2 les asigno el cierre de
        // su jornada. Que el primer barrido las venza no es un efecto colateral del test: es
        // exactamente lo que pasaria en produccion, y es el motivo por el que esa columna se
        // backfilleo. Su reserva -- 50x710 + 60x1500 = 125.500 -- es la diferencia entre el
        // cash contable del seed y el disponible que informa el portfolio.
        var antes = await Portfolio();
        antes.ReservedCash.Should().Be(125_500m);

        var barrido = await Barrer();

        barrido.ExpiredCount.Should().Be(2);

        var despues = await Portfolio();
        despues.ReservedCash.Should().Be(0m);
        despues.AvailableCash.Should().Be(antes.AccountingCash,
            "sin órdenes vivas no queda nada reservado");
    }

    [Fact]
    public async Task El_barrido_vence_exactamente_las_ordenes_vivas_con_la_jornada_terminada()
    {
        await Drenar();

        // Cuatro casos, uno por cada rama del criterio de dominio.
        var vencida = await Submit(Limit());
        var vigente = await Submit(Limit());
        var ejecutada = await Submit(new { userId = UserId, instrumentId = Pamp, side = "BUY", type = "MARKET", size = 5 });
        var cancelada = await Submit(Limit());

        await Client.PostAsync($"/api/orders/{cancelada.Id}/cancel?userId={UserId}", null);

        // Solo estas dos tienen la jornada terminada. La cancelada ademas ya no esta viva:
        // sirve para verificar que el criterio mira el estado y no solo la fecha.
        await Envejecer(vencida.Id, cancelada.Id);

        var barrido = await Barrer();

        barrido.ExpiredCount.Should().Be(1, "solo una estaba viva Y con la jornada terminada");
        (await Estado(vencida.Id)).Should().Be("EXPIRED");
        (await Estado(vigente.Id)).Should().Be("NEW", "su jornada todavia no termino");
        (await Estado(ejecutada.Id)).Should().Be("FILLED", "una MARKET nace sin vencimiento");
        (await Estado(cancelada.Id)).Should().Be("CANCELLED", "ya no estaba viva: vencerla borraria lo que paso");
    }

    [Fact]
    public async Task Vencer_una_orden_libera_su_reserva()
    {
        await Drenar();
        var antes = await Portfolio();

        var order = await Submit(Limit());
        (await Portfolio()).AvailableCash.Should().Be(antes.AvailableCash - 9_000m);

        await Envejecer(order.Id);
        await Barrer();

        (await Portfolio()).AvailableCash.Should().Be(antes.AvailableCash,
            "al dejar de estar viva, la orden deja de restar del disponible");
    }

    [Fact]
    public async Task Correr_el_barrido_dos_veces_no_vence_nada_dos_veces()
    {
        await Drenar();
        var antes = await Portfolio();
        var order = await Submit(Limit());
        await Envejecer(order.Id);

        var primera = await Barrer();
        var segunda = await Barrer();

        primera.ExpiredCount.Should().Be(1);
        segunda.ExpiredCount.Should().Be(0,
            "el filtro por estado hace idempotente al barrido: por eso puede correr en N instancias");

        (await Portfolio()).AvailableCash.Should().Be(antes.AvailableCash);
    }

    [Fact]
    public async Task Una_orden_ya_vencida_no_se_puede_cancelar()
    {
        await Drenar();

        var order = await Submit(Limit());
        await Envejecer(order.Id);
        await Barrer();

        var response = await Client.PostAsync($"/api/orders/{order.Id}/cancel?userId={UserId}", null);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Conflict,
            "el vencimiento ya libero la reserva: cancelar despues la liberaria dos veces");
    }

    // --- Helpers -----------------------------------------------------------------------

    private static object Limit() =>
        new { userId = UserId, instrumentId = Pamp, side = "BUY", type = "LIMIT", size = 10, price = 900 };

    /// <summary>
    /// Vence las órdenes del seed que ya nacieron vencidas, para que cada test mida solo las
    /// suyas. Tiene test propio aparte: aca es preparacion, no lo que se esta probando.
    /// </summary>
    private Task Drenar() => Barrer();

    /// <summary>Dispara el caso de uso por el bus, sin depender del PeriodicTimer del servicio.</summary>
    private async Task<ExpireOrdersResponse> Barrer()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        return await bus.InvokeAsync<ExpireOrdersResponse>(new ExpireOrdersCommand());
    }

    /// <summary>Mueve el vencimiento al pasado: simula que la jornada termino.</summary>
    private async Task Envejecer(params int[] orderIds)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.ExecuteAsync(
            "UPDATE orders SET expiresat = @Ayer WHERE id = ANY(@Ids);",
            new { Ayer = DateTime.UtcNow.AddDays(-1), Ids = orderIds });
    }

    private async Task<string?> Estado(int orderId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        return await connection.ExecuteScalarAsync<string?>(
            "SELECT status FROM orders WHERE id = @orderId;", new { orderId });
    }

    private async Task<OrderResult> Submit(object payload)
    {
        var response = await Client.PostAsJsonAsync("/api/orders", payload);
        return (await response.Content.ReadFromJsonAsync<OrderResult>())!;
    }

    private async Task<PortfolioResult> Portfolio()
        => (await Client.GetFromJsonAsync<PortfolioResult>($"/api/users/{UserId}/portfolio"))!;
}
