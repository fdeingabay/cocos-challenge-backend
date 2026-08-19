namespace Cocos.Domain;

/// <summary>
/// Aritmetica monetaria del dominio. Todo en decimal: con double, floor(monto/precio)
/// devuelve una accion de mas o de menos por error de representacion, y eso es plata real.
/// </summary>
public static class OrderMath
{
    /// <summary>
    /// Cantidad maxima de acciones que entran en un monto. No se admiten fracciones,
    /// asi que se trunca hacia abajo y el remanente de pesos queda sin usar.
    /// </summary>
    public static int SizeFromAmount(decimal amount, decimal price)
    {
        if (price <= 0m) throw new ArgumentOutOfRangeException(nameof(price), price, "El precio debe ser positivo.");
        if (amount <= 0m) return 0;

        return (int)Math.Floor(amount / price);
    }

    /// <summary>
    /// Precio promedio ponderado de compra (PPP). Es el metodo de costeo elegido para el
    /// rendimiento: a diferencia de FIFO no necesita rastrear lotes individuales, asi que
    /// se resuelve con una sola query agregada. La eleccion cambia el numero informado y
    /// esta documentada en el README.
    /// </summary>
    public static decimal? AverageCost(decimal totalBuyCost, int totalBuyQuantity)
        => totalBuyQuantity <= 0 ? null : totalBuyCost / totalBuyQuantity;

    /// <summary>
    /// Rendimiento total de la posicion contra su costo promedio. La cantidad se cancela
    /// en la division, de ahi que baste con comparar precio actual contra PPP.
    /// Es una metrica DEL USUARIO, distinta del retorno diario del instrumento.
    /// </summary>
    public static decimal? TotalReturnPercent(decimal? close, decimal? averageCost)
        => close is null || averageCost is null or 0m
            ? null
            : (close.Value - averageCost.Value) / averageCost.Value * 100m;

    /// <summary>
    /// Retorno diario del instrumento. Es igual para todos los usuarios: no depende de a
    /// que precio compro cada uno. Se informa como campo separado del rendimiento.
    /// </summary>
    public static decimal? DailyReturnPercent(decimal? close, decimal? previousClose)
        => close is null || previousClose is null or 0m
            ? null
            : (close.Value - previousClose.Value) / previousClose.Value * 100m;

    /// <summary>
    /// Vencimiento de una orden LIMIT: cierre de la jornada en que se envio (DAY order).
    /// Recibe el "ahora" ya resuelto por TimeProvider, nunca lee el reloj por su cuenta.
    /// </summary>
    public static DateTime EndOfDay(DateTime timestamp)
        => timestamp.Date.AddDays(1).AddTicks(-1);
}
