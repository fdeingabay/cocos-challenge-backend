using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace Cocos.IntegrationTests;

[Collection(DatabaseCollection.Name)]
public sealed class CancelOrderTests(PostgresFixture fixture) : IntegrationTestBase(fixture)
{
    private const int UserId = 1;
    private const int Pamp = 47;

    [Fact]
    public async Task Cancelar_una_orden_viva_libera_la_reserva()
    {
        var antes = await Portfolio();

        var order = await Submit(new { userId = UserId, instrumentId = Pamp, side = "BUY", type = "LIMIT", size = 10, price = 900 });
        (await Portfolio()).AvailableCash.Should().Be(antes.AvailableCash - 9_000m);

        var response = await Client.PostAsync($"/api/orders/{order.Id}/cancel?userId={UserId}", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        (await Portfolio()).AvailableCash.Should().Be(antes.AvailableCash,
            "al dejar de estar viva, la orden deja de restar del disponible");
    }

    [Fact]
    public async Task Cancelar_dos_veces_la_misma_orden_no_libera_la_reserva_dos_veces()
    {
        var antes = await Portfolio();
        var order = await Submit(new { userId = UserId, instrumentId = Pamp, side = "BUY", type = "LIMIT", size = 10, price = 900 });

        var primera = await Client.PostAsync($"/api/orders/{order.Id}/cancel?userId={UserId}", null);
        var segunda = await Client.PostAsync($"/api/orders/{order.Id}/cancel?userId={UserId}", null);

        primera.StatusCode.Should().Be(HttpStatusCode.OK);
        segunda.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "el UPDATE condicional no afecta filas la segunda vez, y esa es la senal de que perdio la carrera");

        (await Portfolio()).AvailableCash.Should().Be(antes.AvailableCash);
    }

    [Fact]
    public async Task Una_orden_ya_ejecutada_no_se_puede_cancelar()
    {
        var order = await Submit(new { userId = UserId, instrumentId = Pamp, side = "BUY", type = "MARKET", size = 5 });
        order.Status.Should().Be("FILLED");

        var response = await Client.PostAsync($"/api/orders/{order.Id}/cancel?userId={UserId}", null);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "una ejecucion es un hecho consumado: no se puede deshacer cancelando");
    }

    [Fact]
    public async Task Cancelar_una_orden_inexistente_devuelve_404()
    {
        var response = await Client.PostAsync($"/api/orders/999999/cancel?userId={UserId}", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Un_usuario_no_puede_cancelar_la_orden_de_otro()
    {
        var order = await Submit(new { userId = UserId, instrumentId = Pamp, side = "BUY", type = "LIMIT", size = 10, price = 900 });

        var response = await Client.PostAsync($"/api/orders/{order.Id}/cancel?userId=2", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "la orden no existe DENTRO DEL ALCANCE del usuario que pide: no se filtra que exista para otro");
    }

    private async Task<OrderResult> Submit(object payload)
    {
        var response = await Client.PostAsJsonAsync("/api/orders", payload);
        return (await response.Content.ReadFromJsonAsync<OrderResult>())!;
    }

    private async Task<PortfolioResult> Portfolio()
        => (await Client.GetFromJsonAsync<PortfolioResult>($"/api/users/{UserId}/portfolio"))!;
}
