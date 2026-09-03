using System.Net;
using System.Net.Http.Json;
using Dapper;
using FluentAssertions;
using Npgsql;

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

    [Fact]
    public async Task Cancelar_sin_userId_es_un_400_y_no_un_404_que_miente()
    {
        var order = await Submit(new { userId = UserId, instrumentId = Pamp, side = "BUY", type = "LIMIT", size = 10, price = 900 });

        var response = await Client.PostAsync($"/api/orders/{order.Id}/cancel", null);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "falta un parámetro; contestar 404 afirmaria que la orden no existe, y existe");
    }

    [Fact]
    public async Task El_instante_de_la_cancelacion_queda_registrado_y_es_el_que_se_informo()
    {
        var order = await Submit(new { userId = UserId, instrumentId = Pamp, side = "BUY", type = "LIMIT", size = 10, price = 900 });

        var response = await Client.PostAsync($"/api/orders/{order.Id}/cancel?userId={UserId}", null);
        var informado = (await response.Content.ReadFromJsonAsync<CancelResult>())!;

        var registrado = await CancelledAt(order.Id);

        registrado.Should().NotBeNull("informar un instante que no queda en ningun lado lo vuelve irreproducible");
        registrado.Should().BeCloseTo(informado.CancelledAt, TimeSpan.FromMilliseconds(1),
            "es el mismo hecho: lo que se guarda y lo que se contesta no pueden ser dos fechas distintas");
    }

    [Fact]
    public async Task Una_orden_que_no_se_cancelo_no_tiene_fecha_de_cancelacion()
    {
        var order = await Submit(new { userId = UserId, instrumentId = Pamp, side = "BUY", type = "LIMIT", size = 10, price = 900 });

        (await CancelledAt(order.Id)).Should().BeNull("solo una orden cancelada tiene ese dato, y el CHECK de la base lo exige");
    }

    /// <summary>Se lee por fuera de la API a propósito: lo que se verifica es que este en la base.</summary>
    private async Task<DateTime?> CancelledAt(int orderId)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        return await connection.ExecuteScalarAsync<DateTime?>(
            "SELECT cancelledat FROM orders WHERE id = @orderId;", new { orderId });
    }

    private async Task<OrderResult> Submit(object payload)
    {
        var response = await Client.PostAsJsonAsync("/api/orders", payload);
        return (await response.Content.ReadFromJsonAsync<OrderResult>())!;
    }

    private async Task<PortfolioResult> Portfolio()
        => (await Client.GetFromJsonAsync<PortfolioResult>($"/api/users/{UserId}/portfolio"))!;
}
