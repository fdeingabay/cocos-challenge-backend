using Cocos.Domain;
using FluentAssertions;

namespace Cocos.UnitTests;

public class OrderMathTests
{
    [Theory]
    // El caso del enunciado: $100.000 sobre PAMP a 925,85 entran 108 acciones y sobran $8,20.
    [InlineData(100_000, 925.85, 108)]
    // Division exacta: no se pierde ni se gana una accion por redondeo.
    [InlineData(1_000, 100, 10)]
    // Justo por debajo de la siguiente accion.
    [InlineData(999.99, 100, 9)]
    // El monto no alcanza ni para una accion.
    [InlineData(50, 100, 0)]
    public void SizeFromAmount_trunca_siempre_hacia_abajo(decimal amount, decimal price, int expected)
        => OrderMath.SizeFromAmount(amount, price).Should().Be(expected);

    [Fact]
    public void SizeFromAmount_no_admite_precio_no_positivo()
    {
        var act = () => OrderMath.SizeFromAmount(1_000m, 0m);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void SizeFromAmount_con_monto_no_positivo_da_cero()
        => OrderMath.SizeFromAmount(0m, 100m).Should().Be(0);

    [Fact]
    public void AverageCost_pondera_por_cantidad_no_por_operacion()
    {
        // 10 a $100 y 10 a $200 -> PPP 150, no el promedio simple de los precios.
        OrderMath.AverageCost(costBasis: 3_000m, quantity: 20).Should().Be(150m);
    }

    [Fact]
    public void AverageCost_sin_compras_es_null_no_cero()
        => OrderMath.AverageCost(0m, 0).Should().BeNull();

    [Fact]
    public void AverageCost_con_la_posicion_cerrada_es_null_y_no_arrastra_costo()
        // Al cerrar la posicion el costo vuelve a cero: no hay nada contra que medir. Si el
        // costo quedara arrastrado, la proxima compra informaria un PPP que nadie pago.
        => OrderMath.AverageCost(costBasis: 0m, quantity: 0).Should().BeNull();

    [Fact]
    public void TotalReturnPercent_compara_contra_el_PPP()
        => OrderMath.TotalReturnPercent(close: 300m, averageCost: 150m).Should().Be(100m);

    [Fact]
    public void TotalReturnPercent_sin_precio_de_mercado_es_null()
        => OrderMath.TotalReturnPercent(close: null, averageCost: 150m).Should().BeNull();

    [Fact]
    public void TotalReturnPercent_sin_PPP_es_null()
        // Sin compras ejecutadas no hay contra que medir el rendimiento. Pasa en una posicion
        // que se armo vendiendo: AverageCost devuelve null y el rendimiento no existe.
        => OrderMath.TotalReturnPercent(close: 300m, averageCost: null).Should().BeNull();

    [Fact]
    public void TotalReturnPercent_con_PPP_cero_es_null_y_no_divide_por_cero()
        // El PPP es el divisor. Un cero aca no es "rendimiento infinito": es que no hay costo
        // contra el cual comparar, y la respuesta correcta es null y no una excepcion.
        => OrderMath.TotalReturnPercent(close: 300m, averageCost: 0m).Should().BeNull();

    [Fact]
    public void DailyReturnPercent_es_del_instrumento_no_del_usuario()
    {
        // METR el 2023-07-14: close 229,50 sobre previousClose 232,00.
        var result = OrderMath.DailyReturnPercent(229.50m, 232.00m);
        result.Should().BeApproximately(-1.0775m, 0.0001m);
    }

    [Fact]
    public void DailyReturnPercent_con_previousClose_cero_es_null()
        => OrderMath.DailyReturnPercent(100m, 0m).Should().BeNull();

    [Fact]
    public void EndOfDay_vence_al_cierre_del_mismo_dia()
    {
        var enviada = new DateTime(2023, 7, 14, 11, 30, 0, DateTimeKind.Utc);

        var vence = OrderMath.EndOfDay(enviada);

        vence.Date.Should().Be(enviada.Date);
        vence.Should().BeBefore(enviada.Date.AddDays(1));
        vence.Should().BeAfter(enviada);
    }
}
