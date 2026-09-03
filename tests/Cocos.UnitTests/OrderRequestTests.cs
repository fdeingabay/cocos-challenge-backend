using Cocos.Domain;
using Cocos.Domain.Enums;
using FluentAssertions;

namespace Cocos.UnitTests;

public class OrderRequestTests
{
    private static OrderRequest For(int? size, decimal? amount, decimal price, string? key = null)
        => OrderRequest.For(userId: 1, instrumentId: 47, OrderSide.Buy, OrderType.Market,
                            size, amount, price, key);

    [Fact]
    public void Un_size_explicito_se_respeta_tal_cual()
    {
        var request = For(size: 10, amount: null, price: 930m);

        request.Size.Should().Be(10);
        request.Notional.Should().Be(9_300m);
    }

    [Fact]
    public void Un_monto_se_traduce_a_la_maxima_cantidad_entera()
    {
        // 50.000 / 930 = 53,76 -> 53 acciones y 710 pesos que quedan sin usar.
        var request = For(size: null, amount: 50_000m, price: 930m);

        request.Size.Should().Be(53);
        request.Notional.Should().Be(49_290m);
    }

    [Fact]
    public void Un_monto_que_no_alcanza_para_una_accion_no_forma_una_orden()
    {
        var request = For(size: null, amount: 500m, price: 930m);

        request.Size.Should().Be(0);
        request.HasTradeableSize.Should().BeFalse("es un 400, no una orden rechazada");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Una_clave_en_blanco_es_lo_mismo_que_no_mandar_clave(string? key)
    {
        // Tiene que llegar a la base como NULL: el indice unico parcial excluye NULL,
        // pero no la cadena vacia, y dos de esas colisionarian.
        IdempotencyKey.Normalize(key).Should().BeNull();
    }

    [Fact]
    public void La_clave_se_recorta()
    {
        IdempotencyKey.Normalize("  abc-123  ").Should().Be("abc-123");
    }
}
