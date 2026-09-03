using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace Cocos.IntegrationTests;

/// <summary>
/// Primera cobertura propia del listado. Hasta ahora el endpoint solo se usaba como helper de
/// otros tests para contar órdenes, asi que su filtro, su orden y su paginacion nunca se
/// habian ejercitado.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class GetUserOrdersTests(PostgresFixture fixture) : IntegrationTestBase(fixture)
{
    private const int UserId = 1;
    private const int Pamp = 47;

    [Fact]
    public async Task Un_estado_que_no_existe_devuelve_400_y_no_una_lista_vacia()
    {
        var response = await Client.GetAsync($"/api/users/{UserId}/orders?status=BANANA");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "una lista vacia parece un resultado legitimo y esconde el error del cliente");

        var problema = await response.Content.ReadAsStringAsync();
        problema.Should().Contain("BANANA").And.Contain("NEW",
            "el mensaje nombra los estados que si existen");
    }

    [Fact]
    public async Task Un_usuario_inexistente_devuelve_404_y_no_una_lista_vacia()
    {
        var response = await Client.GetAsync("/api/users/999999/orders");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "mismo criterio que el portfolio del mismo usuario inexistente");
    }

    [Fact]
    public async Task El_filtro_acepta_cualquier_capitalizacion()
    {
        var mayúsculas = await Listar("?status=NEW&pageSize=100");
        var minusculas = await Listar("?status=new&pageSize=100");

        minusculas.TotalCount.Should().Be(mayúsculas.TotalCount);
    }

    [Fact]
    public async Task El_filtro_por_estado_devuelve_solo_ese_estado()
    {
        var ejecutada = await Submit(new { userId = UserId, instrumentId = Pamp, side = "BUY", type = "MARKET", size = 5 });
        var viva = await Submit(Limit());

        var nuevas = await Listar("?status=NEW&pageSize=100");
        var ejecutadas = await Listar("?status=FILLED&pageSize=100");

        nuevas.Items.Should().OnlyContain(o => o.Status == "NEW");
        nuevas.Items.Should().Contain(o => o.Id == viva.Id);
        nuevas.Items.Should().NotContain(o => o.Id == ejecutada.Id);

        ejecutadas.Items.Should().OnlyContain(o => o.Status == "FILLED");
        ejecutadas.Items.Should().Contain(o => o.Id == ejecutada.Id);
    }

    [Fact]
    public async Task Sin_filtro_se_listan_todos_los_estados()
    {
        await Submit(new { userId = UserId, instrumentId = Pamp, side = "BUY", type = "MARKET", size = 5 });
        await Submit(Limit());

        var todas = await Listar("?pageSize=100");

        todas.Items.Select(o => o.Status).Distinct().Should().HaveCountGreaterThan(1);
    }

    [Fact]
    public async Task La_orden_mas_reciente_aparece_primera()
    {
        await Submit(Limit());
        var ultima = await Submit(Limit());

        var todas = await Listar("?pageSize=100");

        todas.Items[0].Id.Should().Be(ultima.Id, "se ordena por fecha descendente");
    }

    [Fact]
    public async Task La_paginacion_parte_el_listado_sin_repetir_ni_perder_filas()
    {
        await Submit(Limit());
        await Submit(Limit());

        var primera = await Listar("?page=1&pageSize=1");
        var segunda = await Listar("?page=2&pageSize=1");

        primera.Items.Should().ContainSingle();
        segunda.Items.Should().ContainSingle();
        primera.TotalCount.Should().Be(segunda.TotalCount,
            "el total es del listado completo, no de la pagina");
        primera.TotalCount.Should().BeGreaterThan(1);
        segunda.Items[0].Id.Should().NotBe(primera.Items[0].Id);
    }

    [Fact]
    public async Task El_pageSize_tiene_un_tope_duro()
    {
        var pagina = await Listar("?pageSize=1000");

        pagina.PageSize.Should().Be(100,
            "sin tope, un cliente pide un millon de filas y el endpoint es un vector de DoS");
    }

    // --- Helpers -----------------------------------------------------------------------

    private static object Limit() =>
        new { userId = UserId, instrumentId = Pamp, side = "BUY", type = "LIMIT", size = 1, price = 900 };

    private async Task<Paged<OrderSummary>> Listar(string queryString)
        => (await Client.GetFromJsonAsync<Paged<OrderSummary>>($"/api/users/{UserId}/orders{queryString}"))!;

    private async Task<OrderResult> Submit(object payload)
    {
        var response = await Client.PostAsJsonAsync("/api/orders", payload);
        return (await response.Content.ReadFromJsonAsync<OrderResult>())!;
    }
}
