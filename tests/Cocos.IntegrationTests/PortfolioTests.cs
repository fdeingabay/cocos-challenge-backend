using System.Net;
using System.Net.Http.Json;
using Dapper;
using FluentAssertions;
using Npgsql;

namespace Cocos.IntegrationTests;

/// <summary>
/// Todos los números salen de calcular a mano el seed provisto para el usuario 1.
/// Si alguno de estos asserts falla, el calculo del portfolio cambio de semantica.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class PortfolioTests(PostgresFixture fixture) : IntegrationTestBase(fixture)
{
    /// <summary>Instrumento ACCIONES del seed sin ninguna fila en marketdata.</summary>
    private const int Pgr = 3;

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
        // y SELL 30 FILLED, o sea -10 acciones. La BUY de 60 que "cerraria" el número esta
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
    public async Task Los_pesos_que_informa_el_portfolio_son_exactamente_los_que_decide_el_envio()
    {
        // La costura entre las dos lecturas del invariante. El portfolio INFORMA el disponible
        // y el envio de órdenes DECIDE contra el: las dos cuentas salen de LedgerSql y este
        // test es lo que hace que esa unificacion sea una garantia y no una intención.
        // Si divergieran, la API diria un número y el sistema aceptaria otro.
        const decimal precio = 100m;
        var disponible = (await Portfolio()).AvailableCash;

        var alJusto = (int)(disponible / precio);
        (alJusto * precio).Should().Be(disponible, "el caso limite necesita gastar hasta el último peso");

        var justa = await Submit(alJusto, precio);
        justa.Status.Should().Be("NEW",
            "gastar exactamente el disponible es valido: el limite es inclusivo");

        (await Portfolio()).AvailableCash.Should().Be(0m, "quedo todo comprometido");

        var unaMas = await Submit(1, precio);
        unaMas.Status.Should().Be("REJECTED", "un peso mas que el disponible ya no entra");
    }

    [Fact]
    public async Task Los_nominales_que_informa_el_portfolio_son_exactamente_los_que_decide_la_venta()
    {
        // La otra mitad del invariante: ExecutedQuantity - ReservedQuantity, compartida entre
        // el portfolio y la decision de una venta.
        const int metr = 54;
        var disponibles = (await Portfolio()).Positions.Single(p => p.Ticker == "METR").AvailableQuantity;

        var justa = await Submit(disponibles, precio: 200m, instrumentId: metr, side: "SELL");
        justa.Status.Should().Be("NEW", "vender toda la tenencia libre es valido");

        var posicion = (await Portfolio()).Positions.Single(p => p.Ticker == "METR");
        posicion.AvailableQuantity.Should().Be(0, "la venta viva reserva los nominales");
        posicion.Quantity.Should().Be(disponibles, "pero todavia no se ejecuto: la tenencia no cambio");

        var unaMas = await Submit(1, precio: 200m, instrumentId: metr, side: "SELL");
        unaMas.Status.Should().Be("REJECTED", "una accion mas que la disponible ya no entra");
    }

    [Fact]
    public async Task Una_venta_parcial_no_mueve_el_PPP()
    {
        // Es la regla del PPP y la razon por la que se eligio sobre FIFO: la venta reduce el
        // costo en la misma proporcion que la tenencia, asi que el promedio queda igual.
        var antes = (await Portfolio()).Positions.Single(p => p.Ticker == "PAMP");
        antes.Quantity.Should().Be(40);
        antes.AverageCost.Should().Be(930m);

        await Mercado(10, side: "SELL");

        var despues = (await Portfolio()).Positions.Single(p => p.Ticker == "PAMP");
        despues.Quantity.Should().Be(30);
        despues.AverageCost.Should().Be(930m, "vender no cambia a que precio promedio se compro lo que queda");
    }

    [Fact]
    public async Task Cerrar_y_reabrir_una_posicion_reinicia_el_PPP()
    {
        await Mercado(40, side: "SELL");

        (await Portfolio()).Positions.Should().NotContain(p => p.Ticker == "PAMP",
            "se vendio todo: la posicion se cerro");

        await Mercado(1, side: "BUY");

        var reabierta = (await Portfolio()).Positions.Single(p => p.Ticker == "PAMP");
        reabierta.Quantity.Should().Be(1);
        reabierta.AverageCost.Should().Be(925.85m,
            "es lo unico que se pago por la única accion en cartera; promediar TODAS las compras "
            + "de la historia informaba 929,92, arrastrando una tenencia que ya no existe");
        reabierta.TotalReturnPercent.Should().Be(0m, "se compro al precio de mercado actual");
    }

    [Fact]
    public async Task El_valor_total_de_la_cuenta_suma_el_cash_contable_y_las_posiciones()
    {
        var portfolio = await Portfolio();

        portfolio.TotalAccountValue.Should().Be(889_756.00m,
            "753.000 de cash contable + PAMP 40x925,85 + METR 500x229,50 - BMA 10x1.502,80");

        // El número fija ESTE seed; la identidad fija la regla, y sobrevive a que cambien los datos.
        portfolio.TotalAccountValue.Should().Be(
            portfolio.AccountingCash + portfolio.Positions.Sum(p => p.MarketValue ?? 0m));
    }

    [Fact]
    public async Task Reservar_no_cambia_el_valor_total_de_la_cuenta()
    {
        var antes = await Portfolio();

        await Submit(10, precio: 900m);

        var despues = await Portfolio();

        despues.AvailableCash.Should().Be(antes.AvailableCash - 9_000m, "la reserva compromete pesos");
        despues.TotalAccountValue.Should().Be(antes.TotalAccountValue,
            "pero no los saca de la cuenta: el valor total usa el cash CONTABLE, no el disponible");
    }

    [Fact]
    public async Task Una_posicion_sin_precio_de_mercado_se_informa_sin_valuar()
    {
        // PGR (3) es uno de los dos instrumentos ACCIONES del seed sin ninguna fila en
        // marketdata. La API no puede crear esta posicion -- una MARKET sin close se rechaza
        // con 400 -- asi que solo se llega por una ejecucion historica. Que es justamente como
        // pasa en la realidad: el instrumento deja de cotizar y la tenencia sigue existiendo.
        await Ejecutada(instrumentId: Pgr, size: 20, price: 100m);

        var pgr = (await Portfolio()).Positions.Single(p => p.Ticker == "PGR");

        pgr.Quantity.Should().Be(20, "la tenencia no depende de que haya precio");
        pgr.AverageCost.Should().Be(100m, "el PPP sale de lo que se pago, no del mercado");

        pgr.Close.Should().BeNull();
        pgr.MarketValue.Should().BeNull("valuar 20 nominales sin precio daria un cero que miente");
        pgr.TotalReturnPercent.Should().BeNull("sin precio actual no hay contra que medir");
        pgr.DailyReturnPercent.Should().BeNull();
    }

    /// <summary>
    /// Siembra una compra ya ejecutada, como las del seed. Va por SQL y no por la API a
    /// propósito: el caso que se prueba es precisamente el que la API no puede producir.
    /// </summary>
    private async Task Ejecutada(int instrumentId, int size, decimal price)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);

        await connection.ExecuteAsync(
            """
            INSERT INTO orders (instrumentid, userid, size, filledsize, price, type, side, status, datetime)
            VALUES (@instrumentId, 1, @size, @size, @price, 'MARKET', 'BUY', 'FILLED', @now);
            """,
            new
            {
                instrumentId, size, price,
                // La columna es timestamp sin zona: un Kind=Utc haria que Npgsql infiera timestamptz.
                now = DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Unspecified)
            });
    }

    private async Task<PortfolioResult> Portfolio()
        => (await Client.GetFromJsonAsync<PortfolioResult>("/api/users/1/portfolio"))!;

    /// <summary>Una MARKET, que se ejecuta al instante contra el último close.</summary>
    private async Task<OrderResult> Mercado(int size, string side, int instrumentId = 47)
    {
        var response = await Client.PostAsJsonAsync("/api/orders", new
        {
            userId = 1, instrumentId, side, type = "MARKET", size
        });

        return (await response.Content.ReadFromJsonAsync<OrderResult>())!;
    }

    private async Task<OrderResult> Submit(
        int size, decimal precio, int instrumentId = 47, string side = "BUY")
    {
        var response = await Client.PostAsJsonAsync("/api/orders", new
        {
            userId = 1,
            instrumentId,
            side,
            type = "LIMIT",
            size,
            price = precio
        });

        return (await response.Content.ReadFromJsonAsync<OrderResult>())!;
    }
}
