using System.Net.Http.Json;
using FluentAssertions;

namespace Cocos.IntegrationTests;

/// <summary>
/// El test central de la suite: es el que demuestra que la arquitectura cumple su objetivo.
///
/// Sin el lock de cuenta, N requests simultaneos leen el mismo disponible, cada uno valida
/// contra el saldo completo y todos insertan. El conflicto vive en una SUMA, no en una fila,
/// asi que Postgres no puede detectarlo por su cuenta: ni REPEATABLE READ lo evita.
/// El resultado seria una cuenta en negativo.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ConcurrencyTests(PostgresFixture fixture) : IntegrationTestBase(fixture)
{
    private const int UserId = 1;
    private const int Pamp = 47;
    private const int Metr = 54;

    [Fact]
    public async Task Ordenes_de_compra_simultaneas_no_pueden_gastar_el_mismo_peso_dos_veces()
    {
        var inicial = await Portfolio();
        inicial.AvailableCash.Should().Be(627_500m);

        // Cada orden compromete exactamente $100.000. Entran 6 y sobran $27.500.
        const int intentos = 20;
        const decimal costoUnitario = 100_000m;
        var esperadasAceptadas = (int)(inicial.AvailableCash / costoUnitario); // 6

        var payload = new { userId = UserId, instrumentId = Pamp, side = "BUY", type = "LIMIT", size = 1000, price = 100 };

        var respuestas = await Task.WhenAll(Enumerable.Range(0, intentos).Select(async _ =>
        {
            using var client = Factory.CreateClient();
            var response = await client.PostAsJsonAsync("/api/orders", payload);
            return (await response.Content.ReadFromJsonAsync<OrderResult>())!;
        }));

        respuestas.Count(o => o.Status == "NEW").Should().Be(esperadasAceptadas,
            "solo pueden aceptarse tantas ordenes como entren en el disponible");

        respuestas.Count(o => o.Status == "REJECTED").Should().Be(intentos - esperadasAceptadas);

        var final = await Portfolio();
        final.AvailableCash.Should().Be(inicial.AvailableCash - esperadasAceptadas * costoUnitario);
        final.AvailableCash.Should().BeGreaterThanOrEqualTo(0m,
            "esta es la invariante dura: el disponible no puede quedar negativo bajo ninguna secuencia");
    }

    [Fact]
    public async Task Ventas_simultaneas_no_pueden_comprometer_mas_acciones_de_las_que_hay()
    {
        var inicial = await Portfolio();
        var metr = inicial.Positions.Single(p => p.InstrumentId == Metr);
        metr.AvailableQuantity.Should().Be(500);

        // 10 intentos de vender 100 cada uno sobre una tenencia de 500: entran 5.
        const int intentos = 10;
        var payload = new { userId = UserId, instrumentId = Metr, side = "SELL", type = "LIMIT", size = 100, price = 240 };

        var respuestas = await Task.WhenAll(Enumerable.Range(0, intentos).Select(async _ =>
        {
            using var client = Factory.CreateClient();
            var response = await client.PostAsJsonAsync("/api/orders", payload);
            return (await response.Content.ReadFromJsonAsync<OrderResult>())!;
        }));

        respuestas.Count(o => o.Status == "NEW").Should().Be(5);
        respuestas.Count(o => o.Status == "REJECTED").Should().Be(5);

        var final = await Portfolio();
        var metrFinal = final.Positions.Single(p => p.InstrumentId == Metr);
        metrFinal.AvailableQuantity.Should().Be(0);
        metrFinal.AvailableQuantity.Should().BeGreaterThanOrEqualTo(0,
            "la tenencia disponible tampoco puede quedar negativa: seria una venta en descubierto");
    }

    [Fact]
    public async Task Reintentos_simultaneos_con_la_misma_clave_crean_una_sola_orden()
    {
        // No es concurrencia de base de datos sino del canal: el mismo comando llega N veces.
        // Ningun nivel de aislamiento lo resuelve; solo la idempotencia.
        const string key = "doble-tap-del-usuario";
        var payload = new { userId = UserId, instrumentId = Pamp, side = "BUY", type = "MARKET", size = 5 };

        var respuestas = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ =>
        {
            using var client = Factory.CreateClient();
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
            {
                Content = JsonContent.Create(payload)
            };
            request.Headers.Add("Idempotency-Key", key);

            var response = await client.SendAsync(request);
            return (await response.Content.ReadFromJsonAsync<OrderResult>())!;
        }));

        respuestas.Select(o => o.Id).Distinct().Should().ContainSingle(
            "los 10 reintentos tienen que resolver a la misma orden");
    }

    private async Task<PortfolioResult> Portfolio()
        => (await Client.GetFromJsonAsync<PortfolioResult>($"/api/users/{UserId}/portfolio"))!;
}
