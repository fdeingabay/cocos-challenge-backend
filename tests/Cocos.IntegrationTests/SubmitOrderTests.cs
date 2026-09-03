using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace Cocos.IntegrationTests;

/// <summary>
/// Instrumentos y números vienen del seed provisto (usuario 1, emiliano@test.com):
/// disponible para operar $627.500, PAMP(47) close 925,85, METR(54) 500 acciones.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class SubmitOrderTests(PostgresFixture fixture) : IntegrationTestBase(fixture)
{
    private const int UserId = 1;
    private const int Pamp = 47;
    private const int Metr = 54;

    // ---------- tabla de decision: MARKET ----------

    [Fact]
    public async Task Market_BUY_con_fondos_suficientes_se_ejecuta_al_instante()
    {
        var response = await Submit(new { userId = UserId, instrumentId = Pamp, side = "BUY", type = "MARKET", size = 10 });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var order = await Read(response);
        order.Status.Should().Be("FILLED");
        order.FilledSize.Should().Be(10);
        order.Price.Should().Be(925.85m, "una MARKET se ejecuta contra el último close");
        order.ExpiresAt.Should().BeNull();
    }

    [Fact]
    public async Task Market_BUY_sin_fondos_se_rechaza_y_queda_persistida()
    {
        var response = await Submit(new { userId = UserId, instrumentId = Pamp, side = "BUY", type = "MARKET", size = 1_000_000 });

        response.StatusCode.Should().Be(HttpStatusCode.Created,
            "el request se proceso correctamente; el rechazo es un resultado de negocio, no un error HTTP");

        var order = await Read(response);
        order.Status.Should().Be("REJECTED");
        order.FilledSize.Should().Be(0);
        order.RejectionReason.Should().NotBeNullOrEmpty();

        // El enunciado exige que la orden rechazada quede grabada.
        var listed = await Client.GetFromJsonAsync<Paged<OrderSummary>>(
            $"/api/users/{UserId}/orders?status=REJECTED&pageSize=100");
        listed!.Items.Should().Contain(o => o.Id == order.Id);
    }

    [Fact]
    public async Task Market_SELL_con_tenencia_suficiente_se_ejecuta()
    {
        var response = await Submit(new { userId = UserId, instrumentId = Metr, side = "SELL", type = "MARKET", size = 100 });

        (await Read(response)).Status.Should().Be("FILLED");
    }

    [Fact]
    public async Task Market_SELL_sin_tenencia_se_rechaza()
    {
        var response = await Submit(new { userId = UserId, instrumentId = Metr, side = "SELL", type = "MARKET", size = 100_000 });

        (await Read(response)).Status.Should().Be("REJECTED");
    }

    // ---------- tabla de decision: LIMIT ----------

    [Fact]
    public async Task Limit_BUY_con_fondos_queda_viva_y_reserva()
    {
        var antes = await Portfolio();

        var response = await Submit(new { userId = UserId, instrumentId = Pamp, side = "BUY", type = "LIMIT", size = 10, price = 900 });

        var order = await Read(response);
        order.Status.Should().Be("NEW");
        order.FilledSize.Should().Be(0);
        order.ExpiresAt.Should().NotBeNull("una LIMIT sin vencimiento reservaria fondos para siempre");

        var despues = await Portfolio();
        despues.AccountingCash.Should().Be(antes.AccountingCash, "una orden viva no movio plata todavia");
        despues.AvailableCash.Should().Be(antes.AvailableCash - 9_000m, "pero si la comprometio");
    }

    [Fact]
    public async Task Limit_BUY_sin_fondos_se_rechaza()
    {
        var response = await Submit(new { userId = UserId, instrumentId = Pamp, side = "BUY", type = "LIMIT", size = 10_000, price = 900 });

        (await Read(response)).Status.Should().Be("REJECTED");
    }

    [Fact]
    public async Task Limit_SELL_con_tenencia_queda_viva_y_reserva_los_nominales()
    {
        var response = await Submit(new { userId = UserId, instrumentId = Metr, side = "SELL", type = "LIMIT", size = 100, price = 240 });

        (await Read(response)).Status.Should().Be("NEW");

        var portfolio = await Portfolio();
        var metr = portfolio.Positions.Single(p => p.InstrumentId == Metr);
        metr.Quantity.Should().Be(500, "la tenencia no cambia hasta que la orden se ejecute");
        metr.AvailableQuantity.Should().Be(400, "pero 100 quedan comprometidos");
    }

    [Fact]
    public async Task Limit_SELL_sin_tenencia_se_rechaza()
    {
        var response = await Submit(new { userId = UserId, instrumentId = Metr, side = "SELL", type = "LIMIT", size = 100_000, price = 240 });

        (await Read(response)).Status.Should().Be("REJECTED");
    }

    // ---------- orden por monto ----------

    [Fact]
    public async Task Una_orden_por_monto_compra_la_maxima_cantidad_entera()
    {
        // 100.000 / 925,85 = 108,01 -> 108 acciones, y sobran $8,20 sin usar.
        var response = await Submit(new { userId = UserId, instrumentId = Pamp, side = "BUY", type = "MARKET", amount = 100_000 });

        var order = await Read(response);
        order.Size.Should().Be(108);
        order.Status.Should().Be("FILLED");
    }

    [Fact]
    public async Task Un_monto_que_no_alcanza_para_una_accion_no_forma_orden()
    {
        var response = await Submit(new { userId = UserId, instrumentId = Pamp, side = "BUY", type = "MARKET", amount = 10 });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "no es falta de fondos: es una orden de tamano cero, que ensuciaria el libro si se persistiera");
    }

    [Fact]
    public async Task Enviar_size_y_amount_a_la_vez_es_invalido()
    {
        var response = await Submit(new { userId = UserId, instrumentId = Pamp, side = "BUY", type = "MARKET", size = 5, amount = 5_000 });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Una_LIMIT_sin_precio_es_invalida()
    {
        var response = await Submit(new { userId = UserId, instrumentId = Pamp, side = "BUY", type = "LIMIT", size = 5 });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ---------- el efecto sobre las posiciones ----------
    // "Cuando una orden es ejecutada, se tiene que actualizar el listado de posiciones del
    // usuario" es un requisito del enunciado, y vive en la costura ENTRE los dos endpoints:
    // que la orden quede FILLED y que el portfolio calcule bien son dos cosas distintas de
    // que enviarla mueva la tenencia.

    [Fact]
    public async Task Ejecutar_una_orden_actualiza_la_posicion_que_ya_existia()
    {
        (await Posicion("PAMP")).Quantity.Should().Be(40, "lo que trae el seed");

        await Submit(new { userId = UserId, instrumentId = Pamp, side = "BUY", type = "MARKET", size = 10 });

        var comprada = await Posicion("PAMP");
        comprada.Quantity.Should().Be(50);
        comprada.MarketValue.Should().Be(50 * 925.85m, "el valor de mercado sigue a la cantidad");

        await Submit(new { userId = UserId, instrumentId = Pamp, side = "SELL", type = "MARKET", size = 5 });

        (await Posicion("PAMP")).Quantity.Should().Be(45, "vender tambien mueve la tenencia");
    }

    [Fact]
    public async Task Ejecutar_una_compra_agrega_al_listado_una_posicion_que_no_existia()
    {
        const int dyca = 1;

        (await Portfolio()).Positions.Should().NotContain(p => p.Ticker == "DYCA",
            "el usuario 1 no opero nunca este instrumento");

        await Submit(new { userId = UserId, instrumentId = dyca, side = "BUY", type = "MARKET", size = 3 });

        var nueva = await Posicion("DYCA");
        nueva.Quantity.Should().Be(3);
        nueva.MarketValue.Should().Be(3 * 259.00m);
        nueva.AverageCost.Should().Be(259.00m, "se pago el último close");
    }

    private async Task<PositionResult> Posicion(string ticker)
        => (await Portfolio()).Positions.Single(p => p.Ticker == ticker);

    // ---------- idempotencia ----------

    [Fact]
    public async Task El_mismo_Idempotency_Key_no_crea_una_segunda_orden()
    {
        var payload = new { userId = UserId, instrumentId = Pamp, side = "BUY", type = "MARKET", size = 5 };
        const string key = "reintento-del-cliente-1";

        var primera = await Read(await Submit(payload, key));
        var segunda = await Read(await Submit(payload, key));

        segunda.Id.Should().Be(primera.Id, "el reintento tiene que devolver la orden original, no crear otra");

        var todas = await Client.GetFromJsonAsync<Paged<OrderSummary>>(
            $"/api/users/{UserId}/orders?pageSize=100");
        todas!.Items.Count(o => o.Id == primera.Id).Should().Be(1);
    }

    [Fact]
    public async Task El_reintento_contesta_200_y_no_201_porque_no_creo_nada()
    {
        var payload = new { userId = UserId, instrumentId = Pamp, side = "BUY", type = "MARKET", size = 5 };
        const string key = "reintento-del-cliente-2";

        var alta = await Submit(payload, key);
        var reintento = await Submit(payload, key);

        alta.StatusCode.Should().Be(HttpStatusCode.Created);
        reintento.StatusCode.Should().Be(HttpStatusCode.OK,
            "un 201 le diria al cliente que acaba de crear una orden, y contar altas contaria dos veces la misma compra");

        reintento.Headers.Location.Should().BeNull("no hay recurso nuevo al que apuntar");
        (await Read(reintento)).Id.Should().Be((await Read(alta)).Id);
    }

    [Fact]
    public async Task El_Location_del_201_apunta_a_la_orden_creada()
    {
        var respuesta = await Submit(new { userId = UserId, instrumentId = Pamp, side = "BUY", type = "MARKET", size = 1 });

        var location = respuesta.Headers.Location;
        location.Should().NotBeNull("un 201 sin Location no le dice al cliente donde quedo el recurso");

        // Seguirla tiene que devolver la orden, no un 404 ni un 405. Antes apuntaba a
        // "/api/orders?id=N", que no existe: la cabecera prometia un recurso inexistente.
        var seguida = await Client.GetAsync(location);
        seguida.StatusCode.Should().Be(HttpStatusCode.OK);

        var orden = (await seguida.Content.ReadFromJsonAsync<OrderDetail>())!;
        orden.Id.Should().Be((await Read(respuesta)).Id);
    }

    // ---------- helpers ----------

    private Task<HttpResponseMessage> Submit(object payload, string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = JsonContent.Create(payload)
        };

        if (idempotencyKey is not null)
            request.Headers.Add("Idempotency-Key", idempotencyKey);

        return Client.SendAsync(request);
    }

    private static async Task<OrderResult> Read(HttpResponseMessage response)
        => (await response.Content.ReadFromJsonAsync<OrderResult>())!;

    private async Task<PortfolioResult> Portfolio()
        => (await Client.GetFromJsonAsync<PortfolioResult>($"/api/users/{UserId}/portfolio"))!;
}
