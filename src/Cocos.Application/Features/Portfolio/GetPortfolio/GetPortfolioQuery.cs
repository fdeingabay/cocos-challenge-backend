namespace Cocos.Application.Features.Portfolio.GetPortfolio;

public sealed record GetPortfolioQuery(int UserId);

/// <param name="TotalAccountValue">Cash contable mas el valor de mercado de todas las posiciones.</param>
/// <param name="AvailableCash">Poder de compra: el contable menos lo reservado por las órdenes de compra vivas.</param>
/// <param name="AccountingCash">Saldo puramente contable, sin descontar compromisos.</param>
/// <param name="ReservedCash">Lo que retienen las órdenes LIMIT de compra que siguen vivas.</param>
public sealed record PortfolioResponse(
    int UserId,
    decimal TotalAccountValue,
    decimal AvailableCash,
    decimal AccountingCash,
    decimal ReservedCash,
    IReadOnlyList<PositionResponse> Positions);

/// <param name="Quantity">Acciones en cartera: solo cuenta lo efectivamente ejecutado.</param>
/// <param name="AvailableQuantity">Acciones libres: las de cartera menos las reservadas por órdenes de venta vivas.</param>
/// <param name="AverageCost">Precio promedio ponderado de compra (PPP).</param>
/// <param name="TotalReturnPercent">Rendimiento de la posicion contra su PPP. Es una metrica del usuario.</param>
/// <param name="DailyReturnPercent">Variacion del instrumento en el dia. Es igual para todos los usuarios.</param>
public sealed record PositionResponse(
    int InstrumentId,
    string Ticker,
    string Name,
    int Quantity,
    int AvailableQuantity,
    decimal? Close,
    decimal? MarketValue,
    decimal? AverageCost,
    decimal? TotalReturnPercent,
    decimal? DailyReturnPercent);
