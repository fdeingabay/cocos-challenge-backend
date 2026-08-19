using System.Net;
using System.Net.Http.Json;
using FluentAssertions;

namespace Cocos.IntegrationTests;

/// <summary>
/// Instrumentos y numeros vienen del seed provisto (usuario 1, emiliano@test.com):
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
        order.Price.Should().Be(925.85m, "una MARKET se ejecuta contra el ultimo close");
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
