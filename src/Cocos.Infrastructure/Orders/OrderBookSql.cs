namespace Cocos.Infrastructure.Orders;

/// <summary>
/// Las dos escrituras que cierran una orden viva: cancelarla y vencerla. Cada una es una sola
/// sentencia, y en esa sentencia vive toda la garantia del caso de uso.
/// </summary>
internal static class OrderBookSql
{
    /// <summary>
    /// UPDATE condicional: el estado viaja en el WHERE y no se evalúa antes en memoria. De dos
    /// cancelaciones simultaneas -- o de una cancelación compitiendo con el barrido de
    /// vencimiento -- solo una afecta filas; la otra ve 0 y sabe que perdio la carrera. Sin esto
    /// la reserva se podria liberar dos veces.
    ///
    /// Un unico statement condicional ya es atómico: no hace falta transacción explicita.
    ///
    /// Cuáles son los estados vivos no es un literal de aca: llega por parámetro desde
    /// DbValues.OpenStatuses, para que la regla no quede escrita dos veces, una en C# y otra
    /// en SQL.
    ///
    /// El userid va en el WHERE aunque la pertenencia ya se haya verificado al leer la orden:
    /// no cuesta nada, y una sentencia que toca plata no deberia depender de que el llamador
    /// haya chequeado bien.
    ///
    /// El "cuándo" se escribe en el mismo statement que el "qué": la cancelación es un solo
    /// hecho, y guardar el estado sin el instante deja a la API informando una fecha que no
    /// existe en ningun lado.
    /// </summary>
    public const string CancelIfOpen =
        """
        UPDATE orders
           SET status      = @NewStatus,
               cancelledat = @CancelledAt
         WHERE id     = @OrderId
           AND userid = @UserId
           AND status = ANY(@OpenStatuses);
        """;

    /// <summary>
    /// El barrido de vencimiento: un unico UPDATE masivo condicional, sin usuario ni orden
    /// puntual. Es idempotente por construcción -- el filtro por estado hace que una segunda
    /// corrida afecte 0 filas -- asi que el job puede correr en N instancias sin leader election
    /// ni claim.
    ///
    /// El "expiresat IS NOT NULL" es redundante (NULL &lt;= @AsOf nunca matchea) y se deja igual:
    /// dice que una orden sin vencimiento no vence, que es la regla, en vez de dejarla implicita
    /// en la lógica ternaria de SQL.
    ///
    /// Los estados vivos llegan por parámetro, igual que en la cancelación.
    /// </summary>
    public const string ExpireOpen =
        """
        UPDATE orders
           SET status = @NewStatus
         WHERE status    = ANY(@OpenStatuses)
           AND expiresat IS NOT NULL
           AND expiresat <= @AsOf;
        """;
}
