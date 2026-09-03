using Cocos.Domain.Enums;

namespace Cocos.Domain;

/// <summary>
/// El filtro de estado de una búsqueda de órdenes: o no filtra, o filtra por un estado que
/// existe. Aceptar cualquier string convierte un error del cliente en una lista vacia que
/// parece un resultado legitimo.
/// </summary>
public static class OrderStatusFilter
{
    /// <summary>
    /// Ausente o en blanco significan "sin filtro": devuelve true con null, porque no filtrar es
    /// una peticion valida. Acepta cualquier capitalizacion -- exigir la mayuscula del literal
    /// haria de un detalle de la persistencia parte del contrato.
    /// </summary>
    public static bool TryParse(string? raw, out OrderStatus? status)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            status = null;
            return true;
        }

        if (DbValues.ToOrderStatusOrNull(raw.Trim().ToUpperInvariant()) is { } parsed)
        {
            status = parsed;
            return true;
        }

        status = null;
        return false;
    }

    /// <summary>
    /// El mensaje del 400. Los estados validos salen del enum y no de una lista escrita a mano:
    /// si aparece uno nuevo, el mensaje se actualiza solo.
    /// </summary>
    public static string Unknown(string? raw) =>
        $"El estado '{raw}' no existe. Los validos son: {string.Join(", ", Valid)}.";

    private static IEnumerable<string> Valid =>
        Enum.GetValues<OrderStatus>().Select(status => status.ToDb());
}
