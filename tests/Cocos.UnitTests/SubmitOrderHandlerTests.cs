using Cocos.Application.Common;
using Cocos.Application.Features.Orders.SubmitOrder;
using Cocos.Domain;
using Cocos.Domain.Entities;
using Cocos.Domain.Enums;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace Cocos.UnitTests;

/// <summary>
/// El caso de uso completo, sin base de datos. Es posible porque el lock de cuenta es un
/// objeto detras de una interfaz: antes el handler abria la transacción contra EF y estos
/// caminos solo se podian ejercitar por HTTP contra un Postgres real.
/// </summary>
public class SubmitOrderHandlerTests
{
    private static readonly DateTime Now = new(2023, 7, 14, 12, 0, 0, DateTimeKind.Utc);

    private const int Pamp = 47;

    private readonly FakeAccountLedger _accounts = new();
    private readonly FakeInstrumentReader _instruments = new();

    private SubmitOrderHandler Handler() => new(
        _accounts, _instruments, new SubmitOrderValidator(),
        new FakeTimeProvider(Now));

    private static SubmitOrderCommand Command(
        OrderSide side = OrderSide.Buy,
        OrderType type = OrderType.Market,
        int? size = 10,
        decimal? amount = null,
        decimal? price = null,
        string? key = null) => new(1, Pamp, side, type, size, amount, price, key);

    [Fact]
    public async Task Un_comando_mal_formado_no_llega_a_tomar_el_lock()
    {
        // size y amount a la vez.
        var result = await Handler().Handle(Command(size: 10, amount: 5_000m), default);

        result.Error.Type.Should().Be(ErrorType.Validation);
        _accounts.LockAttempts.Should().Be(0, "validar la forma no requiere serializar la cuenta");
    }

    [Theory]
    [InlineData(OrderSide.CashIn)]
    [InlineData(OrderSide.CashOut)]
    public async Task Una_transferencia_no_se_envia_como_orden_de_mercado(OrderSide side)
    {
        // CASH_IN y CASH_OUT mueven plata contra la cuenta, no contra el mercado: no tienen
        // instrumento que operar ni precio contra el cual valuarse. Que la tabla "orders" los
        // guarde en las mismas filas no los convierte en órdenes enviables.
        var result = await Handler().Handle(Command(side: side), default);

        result.Error.Type.Should().Be(ErrorType.Validation);
        result.Error.Message.Should().Contain("BUY o SELL");
        _accounts.LockAttempts.Should().Be(0, "se rechaza por forma, sin llegar a serializar la cuenta");
    }

    [Fact]
    public async Task Un_usuario_inexistente_devuelve_404()
    {
        _accounts.AccountExists = false;

        var result = await Handler().Handle(Command(), default);

        result.Error.Code.Should().Be("user.not_found");
        result.Error.Type.Should().Be(ErrorType.NotFound);
    }

    [Fact]
    public async Task Un_instrumento_inexistente_devuelve_404_y_libera_el_lock()
    {
        _instruments.Instrument = null;

        var result = await Handler().Handle(Command(), default);

        result.Error.Code.Should().Be("instrument.not_found");
        _accounts.Lock.Committed.Should().BeFalse();
        _accounts.Lock.Disposed.Should().BeTrue("el early return no puede dejar el lock tomado");
    }

    [Fact]
    public async Task Una_moneda_no_se_opera_con_ordenes_de_mercado()
    {
        _instruments.Instrument = new InstrumentSnapshot(66, "ARS", Instrument.CurrencyType);

        var result = await Handler().Handle(Command(), default);

        result.Error.Code.Should().Be("instrument.not_tradable");
        result.Error.Type.Should().Be(ErrorType.Validation);
    }

    [Fact]
    public async Task Una_MARKET_sin_precio_de_mercado_no_se_puede_valuar()
    {
        _instruments.LastClose = null;

        var result = await Handler().Handle(Command(type: OrderType.Market), default);

        result.Error.Code.Should().Be("instrument.no_market_price");
    }

    [Fact]
    public async Task Un_monto_que_no_alcanza_para_una_accion_no_persiste_nada()
    {
        var result = await Handler().Handle(Command(size: null, amount: 500m), default);

        result.Error.Code.Should().Be("order.size_zero");
        _accounts.Lock.Placed.Should().BeNull("la orden no llega a formarse");
        _accounts.Lock.Committed.Should().BeFalse();
    }

    [Fact]
    public async Task Sin_disponible_la_orden_se_persiste_rechazada_y_con_motivo()
    {
        _accounts.Lock.Availability = AccountAvailability.ForBuy(0m);

        var result = await Handler().Handle(Command(), default);

        result.IsSuccess.Should().BeTrue("quedarse sin fondos no es un error del request");
        result.Value.IsReplay.Should().BeFalse("la orden se acaba de crear, aunque sea rechazada");
        result.Value.Order.Status.Should().Be("REJECTED");
        result.Value.Order.RejectionReason.Should().Contain("reservado por órdenes de compra vivas");
        _accounts.Lock.Committed.Should().BeTrue("el enunciado exige persistir la rechazada");
    }

    [Fact]
    public async Task Con_disponible_una_MARKET_se_ejecuta_al_ultimo_close()
    {
        _instruments.LastClose = 930m;
        _accounts.Lock.Availability = AccountAvailability.ForBuy(1_000_000m);

        var result = await Handler().Handle(Command(type: OrderType.Market, size: 10), default);

        result.Value.Order.Status.Should().Be("FILLED");
        result.Value.Order.Price.Should().Be(930m);
        result.Value.Order.Notional.Should().Be(9_300m);
        result.Value.Order.RejectionReason.Should().BeNull();
        _accounts.Lock.Placed!.Status.Should().Be(OrderStatus.Filled);
    }

    [Fact]
    public async Task Una_LIMIT_usa_su_propio_precio_y_no_consulta_el_mercado()
    {
        _accounts.Lock.Availability = AccountAvailability.ForBuy(1_000_000m);

        var result = await Handler().Handle(
            Command(type: OrderType.Limit, size: 10, price: 700m), default);

        result.Value.Order.Status.Should().Be("NEW");
        result.Value.Order.Price.Should().Be(700m);
        _instruments.LastCloseQueries.Should().Be(0, "el precio limite lo trae el pedido");
    }

    [Fact]
    public async Task Un_reintento_con_la_misma_clave_devuelve_la_orden_original()
    {
        _accounts.Lock.ExistingByKey = new SubmitOrderResponse(
            99, 1, Pamp, "PAMP", "BUY", "MARKET", "FILLED", 10, 10, 930m, 9_300m, Now, null, null);

        var result = await Handler().Handle(Command(key: "k-1"), default);

        result.Value.Order.Id.Should().Be(99);
        result.Value.IsReplay.Should().BeTrue(
            "el envio no creo nada: quien traduzca esto a HTTP tiene que poder contestar 200 y no 201");
        _accounts.Lock.Placed.Should().BeNull("no se crea una segunda orden");
    }

    [Fact]
    public async Task La_clave_en_blanco_no_se_usa_para_buscar_duplicados()
    {
        _accounts.Lock.Availability = AccountAvailability.ForBuy(1_000_000m);

        await Handler().Handle(Command(key: "   "), default);

        _accounts.Lock.KeysQueried.Should().ContainSingle().Which.Should().BeNull();
    }

    // --- Fakes -------------------------------------------------------------------------

    private sealed class FakeAccountLedger : IAccountLedger
    {
        public bool AccountExists { get; set; } = true;
        public int LockAttempts { get; private set; }
        public FakeAccountLock Lock { get; } = new();

        public Task<IAccountLock?> LockAsync(int userId, CancellationToken cancellationToken)
        {
            LockAttempts++;
            return Task.FromResult<IAccountLock?>(AccountExists ? Lock : null);
        }
    }

    private sealed class FakeAccountLock : IAccountLock
    {
        public SubmitOrderResponse? ExistingByKey { get; set; }
        public AccountAvailability Availability { get; set; } = AccountAvailability.ForBuy(1_000_000m);
        public Order? Placed { get; private set; }
        public bool Committed { get; private set; }
        public bool Disposed { get; private set; }
        public List<string?> KeysQueried { get; } = [];

        public Task<SubmitOrderResponse?> FindOrderByKeyAsync(string? key, CancellationToken ct)
        {
            KeysQueried.Add(key);
            return Task.FromResult(key is null ? null : ExistingByKey);
        }

        public Task<AccountAvailability> GetAvailabilityAsync(OrderRequest request, CancellationToken ct)
            => Task.FromResult(Availability);

        public void Place(Order order) => Placed = order;

        public Task CommitAsync()
        {
            Committed = true;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeInstrumentReader : IInstrumentReader
    {
        public InstrumentSnapshot? Instrument { get; set; } = new(Pamp, "PAMP", "ACCIONES");
        public decimal? LastClose { get; set; } = 930m;
        public int LastCloseQueries { get; private set; }

        public Task<InstrumentSnapshot?> FindAsync(int instrumentId, CancellationToken ct)
            => Task.FromResult(Instrument);

        public Task<decimal?> GetLastCloseAsync(int instrumentId, CancellationToken ct)
        {
            LastCloseQueries++;
            return Task.FromResult(LastClose);
        }
    }
}
