namespace Cocos.Application.Common;

/// <summary>
/// El invariante del sistema, escrito UNA sola vez.
///
/// No hay saldo almacenado: cash y tenencia son una proyección de la tabla orders, que cumple
/// tres roles a la vez (ledger de ejecutadas, libro de pendientes, log de CASH_IN / CASH_OUT).
/// Estas expresiones son esa proyección.
///
/// Estan aca y no en cada consulta porque las comparten los dos lados que tienen que coincidir
/// SIEMPRE: el portfolio, que INFORMA el disponible, y el envio de órdenes, que DECIDE contra
/// el. Duplicarlas es la clase de repetición que no falla al divergir: la API informa un número
/// y el sistema acepta otro.
///
/// Son fragmentos, no consultas: cada consumidor los compone con el SELECT y el WHERE que
/// necesita. Todos agregan sobre "orders" sin alias de tabla, asi que la consulta que los
/// componga tampoco puede usar uno.
///
/// El único parámetro que esperan -- @OpenStatuses -- sale de DbValues, que es dónde el dominio
/// define que orden sigue viva. Un test fija que las dos listas coincidan.
/// </summary>
public static class LedgerSql
{
    /// <summary>
    /// Pesos que entraron y salieron de la cuenta. NO descuenta reservas: es el saldo contable,
    /// no el disponible.
    ///
    /// Las ramas de BUY y SELL no filtran por estado, y no es un olvido: filledsize ES lo
    /// ejecutado, por definición. Una NEW o una REJECTED lo tienen en cero, y ningun estado
    /// terminal deshace una ejecución -- cancelar el remanente de una orden a medio ejecutar no
    /// devuelve las acciones que ya se compraron. Condicionarlo por estado le devolveria al
    /// usuario los pesos de acciones que si compró y le borraria la posición.
    ///
    /// Lo ejecutado y lo reservado son expresiones separadas y no ramas de un mismo CASE: una
    /// PARTIALLY_FILLED aporta a las dos a la vez, y un CASE se corta en la primera rama
    /// verdadera, asi que la segunda contribución se perdería en silencio.
    /// </summary>
    public const string AccountingCash =
        """
        COALESCE(SUM(CASE
            WHEN side = 'CASH_IN'  AND status = 'FILLED' THEN  size * price
            WHEN side = 'CASH_OUT' AND status = 'FILLED' THEN -size * price
            WHEN side = 'SELL' THEN  filledsize * price
            WHEN side = 'BUY'  THEN -filledsize * price
            ELSE 0 END), 0)
        """;

    /// <summary>
    /// Pesos que retienen las compras vivas. Es el termino que se olvida con facilidad, y sin el
    /// se gasta dos veces el mismo peso: con el seed provisto son $125.500 de diferencia entre
    /// lo que la API informaria y lo que el usuario realmente tiene.
    /// </summary>
    public const string ReservedCash =
        """
        COALESCE(SUM(CASE
            WHEN side = 'BUY' AND status = ANY(@OpenStatuses) THEN (size - filledsize) * price
            ELSE 0 END), 0)
        """;

    /// <summary>
    /// Nominales en cartera: lo comprado menos lo vendido. Cuenta filledsize sin mirar el estado,
    /// por lo mismo que el saldo contable: la ejecución es un hecho consumado y cancelar o vencer
    /// el remanente no la borra.
    /// </summary>
    public const string ExecutedQuantity =
        """
        COALESCE(SUM(CASE
            WHEN side = 'BUY'  THEN  filledsize
            WHEN side = 'SELL' THEN -filledsize
            ELSE 0 END), 0)
        """;

    /// <summary>Nominales que retienen las ventas vivas: la reserva del lado de la tenencia.</summary>
    public const string ReservedQuantity =
        """
        COALESCE(SUM(CASE
            WHEN side = 'SELL' AND status = ANY(@OpenStatuses) THEN size - filledsize
            ELSE 0 END), 0)
        """;
}
