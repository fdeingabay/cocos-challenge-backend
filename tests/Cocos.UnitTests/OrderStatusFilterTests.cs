using Cocos.Domain;
using Cocos.Domain.Enums;
using FluentAssertions;

namespace Cocos.UnitTests;

/// <summary>
/// El filtro de estado es la única parte del listado de órdenes que se puede probar sin base:
/// es donde el texto que manda el cliente se convierte en concepto de dominio.
/// </summary>
public class OrderStatusFilterTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_filtrar_es_una_peticion_valida(string? raw)
    {
        var parsed = OrderStatusFilter.TryParse(raw, out var status);

        parsed.Should().BeTrue();
        status.Should().BeNull("ausente y en blanco son lo mismo: traer todas");
    }

    [Theory]
    [InlineData("NEW")]
    [InlineData("new")]
    [InlineData("  New  ")]
    public void La_capitalizacion_del_literal_no_es_parte_del_contrato(string raw)
    {
        OrderStatusFilter.TryParse(raw, out var status).Should().BeTrue();

        status.Should().Be(OrderStatus.New,
            "exigir mayúsculas convertiria un detalle de la persistencia en contrato publico");
    }

    [Fact]
    public void Todos_los_estados_del_dominio_se_pueden_filtrar()
    {
        foreach (var expected in Enum.GetValues<OrderStatus>())
        {
            OrderStatusFilter.TryParse(expected.ToDb(), out var status).Should().BeTrue();
            status.Should().Be(expected);
        }
    }

    [Theory]
    [InlineData("BANANA")]
    [InlineData("PARTIALLY")]
    [InlineData("0")]
    public void Un_estado_que_no_existe_no_parsea(string raw)
    {
        OrderStatusFilter.TryParse(raw, out var status).Should().BeFalse(
            "aceptarlo lo convertiria en una lista vacia que parece un resultado legitimo");
        status.Should().BeNull();
    }

    [Fact]
    public void El_mensaje_de_error_enumera_los_estados_validos()
    {
        var mensaje = OrderStatusFilter.Unknown("BANANA");

        mensaje.Should().Contain("BANANA");
        foreach (var status in Enum.GetValues<OrderStatus>())
            mensaje.Should().Contain(status.ToDb(),
                "la lista se arma desde el enum: un estado nuevo aparece solo");
    }

    [Fact]
    public void Un_literal_leido_de_la_base_que_el_dominio_no_modela_es_un_fallo_inesperado()
    {
        // La otra puerta, la de adentro: ahi un valor desconocido no es error del cliente.
        var traducir = () => DbValues.ToOrderStatus("BANANA");

        traducir.Should().Throw<ArgumentOutOfRangeException>();
    }
}
