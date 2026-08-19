using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace Cocos.IntegrationTests;

/// <summary>
/// Todos los numeros salen de calcular a mano el seed provisto para el usuario 1.
/// Si alguno de estos asserts falla, el calculo del portfolio cambio de semantica.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class PortfolioTests(PostgresFixture fixture) : IntegrationTestBase(fixture)
{
    [Fact]
    public async Task El_disponible_descuenta_las_ordenes_vivas()
    {
        var portfolio = await Client.GetFromJsonAsync<PortfolioResult>("/api/users/1/portfolio");

        portfolio!.AccountingCash.Should().Be(753_000m,
            "CASH_IN 1.000.000 - CASH_OUT 100.000 - compras 202.300 + ventas 55.300");

        portfolio.ReservedCash.Should().Be(125_500m,
            "LOMA 50x710 = 35.500 mas BMA 60x1500 = 90.000, ambas LIMIT en NEW");

        portfolio.AvailableCash.Should().Be(627_500m,
            "sin descontar las reservas la API informaria 125.500 de mas");
    }

    [Fact]
    public async Task Las_posiciones_se_calculan_solo_con_ordenes_ejecutadas()
    {
        var portfolio = await Client.GetFromJsonAsync<PortfolioResult>("/api/users/1/portfolio");

        var pamp = portfolio!.Positions.Single(p => p.Ticker == "PAMP");
        pamp.Quantity.Should().Be(40, "compro 50 y vendio 10");
        pamp.Close.Should().Be(925.85m);
        pamp.MarketValue.Should().Be(37_034.00m);
        pamp.AverageCost.Should().Be(930m);

        var metr = portfolio.Positions.Single(p => p.Ticker == "METR");
        metr.Quantity.Should().Be(500);
        metr.MarketValue.Should().Be(114_750.00m);
    }

    [Fact]
    public async Task El_rendimiento_se_mide_contra_el_PPP_y_el_retorno_diario_contra_previousClose()
    {
        var portfolio = await Client.GetFromJsonAsync<PortfolioResult>("/api/users/1/portfolio");

        var metr = portfolio!.Positions.Single(p => p.Ticker == "METR");

        // Compro a 250, hoy vale 229,50 -> -8,20%. Es una metrica del usuario.
        metr.TotalReturnPercent.Should().BeApproximately(-8.20m, 0.01m);

        // El instrumento cayo de 232,00 a 229,50 -> -1,08%. Es igual para todos.
        metr.DailyReturnPercent.Should().BeApproximately(-1.08m, 0.01m);

        metr.TotalReturnPercent.Should().NotBe(metr.DailyReturnPercent,
            "son dos metricas distintas y confundirlas es el error clasico de este calculo");
    }

    [Fact]
    public async Task El_cash_no_aparece_como_una_posicion_mas()
    {
        var portfolio = await Client.GetFromJsonAsync<PortfolioResult>("/api/users/1/portfolio");

        portfolio!.Positions.Should().NotContain(p => p.Ticker == "ARS",
            "el ARS es un instrumento MONEDA: es el cash, y se informa como pesos disponibles");
    }

    [Fact]
    public async Task El_seed_provisto_arrastra_una_posicion_negativa_en_BMA()
    {
        // Hallazgo sobre los datos provistos, no un bug de la API: BMA tiene BUY 20 FILLED
        // y SELL 30 FILLED, o sea -10 acciones. La BUY de 60 que "cerraria" el numero esta
        // en NEW, y el enunciado exige calcular la tenencia solo con las ejecutadas.
        // Se documenta en el README. La API no puede generar este estado: lo hereda.
        var portfolio = await Client.GetFromJsonAsync<PortfolioResult>("/api/users/1/portfolio");

        portfolio!.Positions.Single(p => p.Ticker == "BMA").Quantity.Should().Be(-10);
    }

    [Fact]
    public async Task Un_usuario_inexistente_devuelve_404_y_no_un_portfolio_vacio()
    {
        var response = await Client.GetAsync("/api/users/9999/portfolio");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "un portfolio vacio y un usuario que no existe no son lo mismo");
    }

    [Fact]
    public async Task La_busqueda_de_instrumentos_pagina_y_matchea_por_ticker_y_por_nombre()
    {
        var porTicker = await Client.GetFromJsonAsync<Paged<InstrumentResult>>("/api/instruments?search=PAMP");
        porTicker!.Items.Should().ContainSingle(i => i.Ticker == "PAMP");

        var porNombre = await Client.GetFromJsonAsync<Paged<InstrumentResult>>("/api/instruments?search=pampa");
        porNombre!.Items.Should().Contain(i => i.Ticker == "PAMP", "la busqueda tambien es por nombre y es case-insensitive");

        var primera = await Client.GetFromJsonAsync<Paged<InstrumentResult>>("/api/instruments?page=1&pageSize=5");
        primera!.Items.Should().HaveCount(5);
        primera.TotalCount.Should().BeGreaterThan(5);
    }

    [Fact]
    public async Task El_pageSize_esta_acotado_por_arriba()
    {
        var response = await Client.GetFromJsonAsync<Paged<InstrumentResult>>("/api/instruments?pageSize=100000");

        response!.PageSize.Should().BeLessThanOrEqualTo(100,
            "sin tope, un cliente puede pedir toda la tabla en un request");
    }

    private sealed record InstrumentResult(int Id, string Ticker, string Name, string Type);
}
