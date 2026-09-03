using Cocos.Application.Common;
using FluentAssertions;

namespace Cocos.UnitTests;

/// <summary>
/// Parametrizar la consulta evita la inyeccion SQL, pero dentro de un LIKE el valor sigue
/// siendo sintaxis. Estos tests fijan que el termino se busque literal.
/// </summary>
public class LikePatternTests
{
    [Fact]
    public void Un_termino_comun_queda_rodeado_de_comodines()
    {
        LikePattern.Contains("pampa").Should().Be("%pampa%");
    }

    [Fact]
    public void El_porcentaje_deja_de_ser_comodin()
    {
        // Sin esto, buscar "%" devuelve la tabla entera: el patrón queda "%%%".
        LikePattern.Contains("%").Should().Be(@"%\%%");
    }

    [Fact]
    public void El_guion_bajo_deja_de_ser_comodin()
    {
        // Sin esto, "S_A" matchea "S.A." -- 39 de los 66 instrumentos del seed.
        LikePattern.Contains("S_A").Should().Be(@"%S\_A%");
    }

    [Fact]
    public void La_barra_se_escapa_primero_para_no_escapar_lo_ya_escapado()
    {
        // Si la barra se escapara al final, el resultado de "50%" seria "%50\\%%" -- la barra
        // que introdujo el escape del porcentaje quedaria escapada a su vez y el patrón
        // buscaria una barra literal.
        LikePattern.Contains(@"50%").Should().Be(@"%50\%%");
        LikePattern.Contains(@"a\b").Should().Be(@"%a\\b%");
    }

    [Fact]
    public void Un_termino_que_ya_parece_un_patron_se_busca_literal()
    {
        LikePattern.Contains("%_%").Should().Be(@"%\%\_\%%");
    }

    [Fact]
    public void Los_puntos_y_demas_caracteres_no_se_tocan()
    {
        // Solo '%', '_' y la barra son sintaxis de LIKE. Escapar de mas romperia búsquedas
        // legitimas: "Y.P.F. S.A." tiene que seguir matcheando tal cual.
        LikePattern.Contains("Y.P.F. S.A.").Should().Be("%Y.P.F. S.A.%");
    }
}
