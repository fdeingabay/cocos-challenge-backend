using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace Cocos.IntegrationTests;

/// <summary>
/// Lectura de una orden puntual. El endpoint existe porque el 201 del alta devuelve un
/// Location, y una cabecera que apunta a un recurso inexistente es peor que no mandarla.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class GetOrderTests(PostgresFixture fixture) : IntegrationTestBase(fixture)
{
    private const int UserId = 1;
    private const int Pamp = 47;

    [Fact]
    public async Task Una_orden_propia_se_lee_con_todos_sus_valores()
    {
        var creada = await Submit(size: 10, price: 900m);

        var orden = (await Client.GetFromJsonAsync<OrderDetail>($"/api/orders/{creada.Id}?userId={UserId}"))!;

        orden.Id.Should().Be(creada.Id);
        orden.Ticker.Should().Be("PAMP");
        (orden.Side, orden.Type, orden.Status).Should().Be(("BUY", "LIMIT", "NEW"));
        (orden.Size, orden.Price, orden.Notional).Should().Be((10, 900m, 9_000m));
        orden.ExpiresAt.Should().NotBeNull("una LIMIT viva vence al cierre de la jornada");
        orden.CancelledAt.Should().BeNull("no se cancelo");
    }

    [Fact]
    public async Task Cancelar_deja_el_instante_visible_en_la_orden()
    {
        var creada = await Submit(size: 10, price: 900m);
        await Client.PostAsync($"/api/orders/{creada.Id}/cancel?userId={UserId}", null);

        var orden = (await Client.GetFromJsonAsync<OrderDetail>($"/api/orders/{creada.Id}?userId={UserId}"))!;

        orden.Status.Should().Be("CANCELLED");
        orden.CancelledAt.Should().NotBeNull(
            "la columna existia y no habia forma de leerla por la API: el dato quedaba escrito y ciego");
    }

    [Fact]
    public async Task La_orden_de_otro_usuario_devuelve_404_y_no_403()
    {
        var creada = await Submit(size: 10, price: 900m);

        var response = await Client.GetAsync($"/api/orders/{creada.Id}?userId=2");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "un 403 confirmaria que la orden existe para otro; el alcance es del usuario que pregunta");
    }

    [Fact]
    public async Task Una_orden_inexistente_devuelve_404()
    {
        var response = await Client.GetAsync($"/api/orders/999999?userId={UserId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Sin_userId_es_un_400_y_no_un_404()
    {
        var creada = await Submit(size: 10, price: 900m);

        var response = await Client.GetAsync($"/api/orders/{creada.Id}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "el alcance es obligatorio: sin el, el 0 por defecto convierte un parámetro faltante en un 404");
    }

    private async Task<OrderResult> Submit(int size, decimal price)
    {
        var response = await Client.PostAsJsonAsync("/api/orders", new
        {
            userId = UserId, instrumentId = Pamp, side = "BUY", type = "LIMIT", size, price
        });

        return (await response.Content.ReadFromJsonAsync<OrderResult>())!;
    }
}
