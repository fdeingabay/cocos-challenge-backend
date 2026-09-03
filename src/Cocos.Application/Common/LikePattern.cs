namespace Cocos.Application.Common;

/// <summary>
/// Patrones LIKE/ILIKE armados a partir de texto que escribió un usuario.
///
/// Parametrizar evita la inyeccion SQL pero no alcanza para que el termino sea un dato: dentro
/// de un LIKE el valor sigue siendo sintaxis. '%' y '_' son comodines, asi que sin escapar,
/// buscar "%" devuelve la tabla entera y buscar "S_A" devuelve todo lo que diga "S.A." -- el
/// guión bajo matchea el punto.
///
/// Vive en Application y no en Domain: los comodines de LIKE son sintaxis del motor, no un
/// concepto de negocio.
/// </summary>
public static class LikePattern
{
    /// <summary>Caracter de escape. Es el default de Postgres; las sentencias lo declaran igual.</summary>
    public const string EscapeCharacter = "\\";

    /// <summary>Patron que matchea el termino en cualquier posicion, tratandolo como literal.</summary>
    public static string Contains(string term) => $"%{Escape(term)}%";

    /// <summary>
    /// Neutraliza los comodines para que el termino se busque literal.
    ///
    /// La barra va PRIMERO: escapándola al final, las barras que agregan los otros dos
    /// reemplazos se volverían a escapar y el patrón quedaria mal formado -- "50%" terminaria
    /// buscando "50\\%" en vez de "50%".
    /// </summary>
    public static string Escape(string term) => term
        .Replace(EscapeCharacter, EscapeCharacter + EscapeCharacter)
        .Replace("%", EscapeCharacter + "%")
        .Replace("_", EscapeCharacter + "_");
}
