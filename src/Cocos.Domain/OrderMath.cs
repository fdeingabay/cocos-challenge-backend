namespace Cocos.Domain;

/// <summary>
/// Aritmetica monetaria del dominio. Todo en decimal: con double, floor(monto/precio) da una
/// accion de mas o de menos por error de representacion, y eso es plata real.
/// </summary>
public static class OrderMath
{
    /// <summary>
    /// Cuantas acciones entran en un monto. No hay fracciones: trunca hacia abajo y el remanente
    /// de pesos queda sin usar. Con un precio no positivo lanza
    /// <see cref="ArgumentOutOfRangeException"/> -- valuar contra ese precio es un fallo, no una
    /// orden invalida.
    /// </summary>
    public static int SizeFromAmount(decimal amount, decimal price)
    {
        if (price <= 0m) throw new ArgumentOutOfRangeException(nameof(price), price, "El precio debe ser positivo.");
        if (amount <= 0m) return 0;

        return (int)Math.Floor(amount / price);
    }

    /// <summary>
    /// Precio promedio ponderado de compra (PPP) de la tenencia ACTUAL. Es el metodo de costeo
    /// elegido: cambia el rendimiento informado, y la eleccion esta justificada en el README.
    ///
    /// Recibe el costo de lo que hoy esta en cartera, no la suma de todas las compras de la
    /// historia. Bajo PPP una venta baja el costo en la misma proporcion que la tenencia -- el
    /// promedio no se mueve -- y al cerrar la posicion el costo vuelve a cero. Promediar toda la
    /// historia coincide con eso solo mientras no haya ventas; despues arrastra compras de una
    /// tenencia que ya no existe. El recorrido ordenado lo hace el CTE recursivo de la consulta
    /// de portfolio.
    /// </summary>
    public static decimal? AverageCost(decimal costBasis, int quantity)
        => quantity <= 0 ? null : costBasis / quantity;

    /// <summary>
    /// Rendimiento de la posicion contra su PPP. La cantidad se cancela en la division, por eso
    /// alcanza con comparar precio actual contra PPP. Es una metrica DEL USUARIO, distinta de la
    /// variacion diaria del instrumento.
    /// </summary>
    public static decimal? TotalReturnPercent(decimal? close, decimal? averageCost)
        => close is null || averageCost is null or 0m
            ? null
            : (close.Value - averageCost.Value) / averageCost.Value * 100m;

    /// <summary>
    /// Variacion diaria del instrumento. Es igual para todos los usuarios: no depende de a que
    /// precio compro cada uno, y por eso se informa aparte del rendimiento.
    /// </summary>
    public static decimal? DailyReturnPercent(decimal? close, decimal? previousClose)
        => close is null || previousClose is null or 0m
            ? null
            : (close.Value - previousClose.Value) / previousClose.Value * 100m;

    /// <summary>
    /// Vencimiento de una orden LIMIT: el cierre de la jornada en que se envio (DAY order).
    /// Recibe el "ahora" ya resuelto por TimeProvider, nunca lee el reloj por su cuenta.
    /// </summary>
    public static DateTime EndOfDay(DateTime timestamp)
        => timestamp.Date.AddDays(1).AddTicks(-1);
}
