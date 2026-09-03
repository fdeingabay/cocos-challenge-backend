namespace Cocos.Domain;

/// <summary>
/// Normaliza la clave de idempotencia. "Sin clave" y "clave en blanco" son lo mismo y tienen
/// que llegar a la base como NULL.
/// </summary>
public static class IdempotencyKey
{
    /// <summary>
    /// Colapsa null, vacio y espacios a null; al resto le recorta los bordes.
    ///
    /// El binding de ASP.NET ya colapsa el header vacio, pero confiar en eso ata el dominio al
    /// transporte: por cualquier otra via una clave en blanco pasaria el chequeo de duplicados,
    /// y dos de esas chocarian contra el indice unico parcial, que excluye NULL pero no la
    /// cadena vacia.
    /// </summary>
    public static string? Normalize(string? key)
        => string.IsNullOrWhiteSpace(key) ? null : key.Trim();
}
