using System.Net;
using System.Net.Http.Json;
using Cocos.Application.Features.Orders.ExpireOrders;
using Dapper;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Wolverine;

namespace Cocos.IntegrationTests;

/// <summary>
/// El ciclo de vida de una orden que se ejecuto a medias.
///
/// Es el unico estado que la API todavia no produce por si sola -- no hay matching engine --
/// pero el sistema entero esta construido para soportarlo: la columna filledsize, la nocion de
/// remanente, y la cancelación habilitada para PARTIALLY_FILLED. Estos tests fijan la regla que
/// hace que esas piezas encajen: UNA EJECUCION ES UN HECHO CONSUMADO. Cancelar o vencer libera
/// el remanente y nada mas; lo ya ejecutado no se toca.
///
/// Sin esto el ledger condicionaba lo ejecutado a un conjunto de estados, y al pasar a CANCELLED
/// el filledsize dejaba de contar: el usuario recuperaba los pesos de acciones que si habia
/// comprado y la posicion desaparecia. Plata creada de la nada.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class PartialFillTests(PostgresFixture fixture) : IntegrationTestBase(fixture)
{
    // El usuario 2 no tiene ninguna orden en el seed: hoja en blanco para armar el escenario.
    private const int Usuario = 2;
    private const int Ggal = 34;
    private const int Ars = 66;

    private const decimal Precio = 900m;
    private const int Pedidas = 10;
    private const int Ejecutadas = 4;

    private const decimal Deposito = 100_000m;
    private const decimal Gastado = Ejecutadas * Precio;          // 3.600 realmente pagados
    private const decimal Contable = Deposito - Gastado;          // 96.400
    private const decimal Reservado = (Pedidas - Ejecutadas) * Precio; // 5.400 por el remanente

    [Fact]
    public async Task Una_orden_a_medio_ejecutar_informa_lo_ejecutado_y_reserva_solo_el_remanente()
    {
        await SembrarEjecucionParcial(vencimiento: EnElFuturo);

        var portfolio = await Portfolio();

        portfolio.AccountingCash.Should().Be(Contable, "se pagaron 4 acciones a 900");
        portfolio.ReservedCash.Should().Be(Reservado, "las otras 6 siguen comprometidas");
        portfolio.AvailableCash.Should().Be(Contable - Reservado);

        var ggal = portfolio.Positions.Single(p => p.Ticker == "GGAL");
        ggal.Quantity.Should().Be(Ejecutadas, "en cartera estan las que se ejecutaron, no las que se pidieron");
        ggal.AverageCost.Should().Be(Precio);
    }

    [Fact]
    public async Task Cancelar_el_remanente_no_borra_lo_que_ya_se_ejecuto()
    {
        var orden = await SembrarEjecucionParcial(vencimiento: EnElFuturo);

        var respuesta = await Client.PostAsync($"/api/orders/{orden}/cancel?userId={Usuario}", null);
        respuesta.StatusCode.Should().Be(HttpStatusCode.OK);

        await LoEjecutadoSigueEnPie("cancelar libera el remanente, no deshace la compra");
    }

    [Fact]
    public async Task Vencer_el_remanente_no_borra_lo_que_ya_se_ejecuto()
    {
        await SembrarEjecucionParcial(vencimiento: EnElPasado);

        await Barrer();

        // El vencimiento es la version peligrosa del mismo bug: no lo dispara el usuario sino
        // un job, asi que la plata se creaba sola cada cinco minutos.
        await LoEjecutadoSigueEnPie("vencer el remanente tampoco deshace la compra");
    }

    private async Task LoEjecutadoSigueEnPie(string porque)
    {
        var portfolio = await Portfolio();

        portfolio.AccountingCash.Should().Be(Contable, porque);
        portfolio.ReservedCash.Should().Be(0m, "el remanente dejo de estar vivo");
        portfolio.AvailableCash.Should().Be(Contable,
            "el disponible sube exactamente lo reservado -- ni un peso mas, que seria plata de la nada");

        var ggal = portfolio.Positions.Single(p => p.Ticker == "GGAL");
        ggal.Quantity.Should().Be(Ejecutadas, porque);
        ggal.AvailableQuantity.Should().Be(Ejecutadas, "ya no queda nada reservado sobre ellas");
        ggal.AverageCost.Should().Be(Precio, "el PPP tampoco olvida lo que se pago");
    }

    // ---------- armado del escenario ----------

    private static readonly DateTime EnElFuturo = new(2099, 12, 31, 23, 59, 59, DateTimeKind.Unspecified);
    private static readonly DateTime EnElPasado = new(2023, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);

    /// <summary>
    /// Un deposito y una compra ejecutada a medias. Va por SQL porque es justamente el estado
    /// que la API no sabe producir: sin matching engine, nada genera un fill parcial.
    /// </summary>
    private async Task<int> SembrarEjecucionParcial(DateTime vencimiento)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);

        await connection.ExecuteAsync(
            """
            INSERT INTO orders (instrumentid, userid, size, filledsize, price, type, side, status, datetime)
            VALUES (@ars, @usuario, @deposito, @deposito, 1, 'MARKET', 'CASH_IN', 'FILLED', @alta);
            """,
            new { ars = Ars, usuario = Usuario, deposito = (int)Deposito, alta = EnElPasado });

        return await connection.ExecuteScalarAsync<int>(
            """
            INSERT INTO orders (instrumentid, userid, size, filledsize, price, type, side, status, datetime, expiresat)
            VALUES (@ggal, @usuario, @pedidas, @ejecutadas, @precio, 'LIMIT', 'BUY', 'PARTIALLY_FILLED', @alta, @vencimiento)
            RETURNING id;
            """,
            new
            {
                ggal = Ggal, usuario = Usuario, pedidas = Pedidas, ejecutadas = Ejecutadas,
                precio = Precio, alta = EnElPasado, vencimiento
            });
    }

    private async Task Barrer()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();

        await bus.InvokeAsync<ExpireOrdersResponse>(new ExpireOrdersCommand());
    }

    private async Task<PortfolioResult> Portfolio()
        => (await Client.GetFromJsonAsync<PortfolioResult>($"/api/users/{Usuario}/portfolio"))!;
}
