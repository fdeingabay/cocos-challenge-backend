using System.Net.Http.Json;
using FluentAssertions;

namespace Cocos.IntegrationTests;

/// <summary>
/// búsqueda de instrumentos. Los dos primeros tests vivian en PortfolioTests: se mudaron aca,
/// que es donde corresponde. El resto cubre el escape de comodines, que faltaba.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class SearchInstrumentsTests(PostgresFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task La_búsqueda_matchea_por_ticker_y_por_nombre()
    {
        var porTicker = await Buscar("?search=PAMP");
        porTicker.Items.Should().ContainSingle(i => i.Ticker == "PAMP");

        var porNombre = await Buscar("?search=pampa");
        porNombre.Items.Should().Contain(i => i.Ticker == "PAMP",
            "la búsqueda tambien es por nombre y es case-insensitive");
    }

    [Theory]
    [InlineData("zorraquin", "GARO")]
    [InlineData("compania", "INTR")]
    public async Task Buscar_sin_tildes_encuentra_igual(string termino, string ticker)
    {
        // Nadie escribe las tildes en un buscador. Devolver cero resultados por una tilde es un
        // error de producto: el instrumento existe y el usuario escribio bien su nombre.
        var sinTilde = await Buscar($"?search={termino}");

        sinTilde.Items.Should().Contain(i => i.Ticker == ticker);
    }

    [Fact]
    public async Task Buscar_con_tildes_sigue_funcionando()
    {
        // El acento se normaliza en los DOS lados, asi que quien las escribe no queda afuera.
        var conTilde = await Buscar("?search=Zorraqu%C3%ADn");

        conTilde.Items.Should().Contain(i => i.Ticker == "GARO");
    }

    [Fact]
    public async Task La_búsqueda_pagina()
    {
        var primera = await Buscar("?page=1&pageSize=5");

        primera.Items.Should().HaveCount(5);
        primera.TotalCount.Should().BeGreaterThan(5);
    }

    [Fact]
    public async Task El_pageSize_esta_acotado_por_arriba()
    {
        var response = await Buscar("?pageSize=100000");

        response.PageSize.Should().BeLessThanOrEqualTo(100,
            "sin tope, un cliente puede pedir toda la tabla en un request");
    }

    [Fact]
    public async Task El_porcentaje_se_busca_literal_y_no_como_comodin()
    {
        var todos = await Buscar("?pageSize=100");
        var porcentaje = await Buscar("?search=%25&pageSize=100");

        todos.TotalCount.Should().BeGreaterThan(0);
        porcentaje.TotalCount.Should().Be(0,
            "ningun instrumento tiene un % en el ticker ni en el nombre; sin escapar devolvia la tabla entera");
    }

    [Fact]
    public async Task El_guion_bajo_se_busca_literal_y_no_como_comodin()
    {
        // Sin escapar, el guion bajo matcheaba el punto de "S.A." y devolvia 39 de 66.
        var guionBajo = await Buscar("?search=S_A&pageSize=100");

        guionBajo.TotalCount.Should().Be(0);

        // Control: el termino que el comodin estaba matcheando por accidente sigue funcionando.
        var punto = await Buscar("?search=S.A&pageSize=100");
        punto.TotalCount.Should().BeGreaterThan(0, "buscar el punto literal tiene que seguir andando");
    }

    [Fact]
    public async Task Escapar_no_rompe_las_búsquedas_legitimas()
    {
        var conPuntos = await Buscar("?search=Y.P.F.&pageSize=100");

        conPuntos.Items.Should().Contain(i => i.Ticker == "YPFD",
            "solo %, _ y la barra son sintaxis de LIKE: el resto no se toca");
    }

    [Fact]
    public async Task Sin_termino_se_devuelve_el_listado_completo_paginado()
    {
        var todos = await Buscar("?pageSize=100");
        var enBlanco = await Buscar("?search=%20%20&pageSize=100");

        enBlanco.TotalCount.Should().Be(todos.TotalCount,
            "un termino en blanco es lo mismo que no filtrar");
    }

    private async Task<Paged<InstrumentResult>> Buscar(string queryString)
        => (await Client.GetFromJsonAsync<Paged<InstrumentResult>>($"/api/instruments{queryString}"))!;
}
