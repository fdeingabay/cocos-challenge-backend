namespace Cocos.IntegrationTests;

// Contratos propios del test, deliberadamente desacoplados de los de produccion:
// si alguien renombra un campo de la respuesta, estos tests tienen que fallar.

public sealed record OrderResult(
    int Id, int UserId, int InstrumentId, string Ticker, string Side, string Type,
    string Status, int Size, int FilledSize, decimal Price, decimal Notional,
    DateTime DateTime, DateTime? ExpiresAt, string? RejectionReason);

public sealed record OrderDetail(
    int Id, int UserId, int InstrumentId, string Ticker, string Side, string Type,
    string Status, int Size, int FilledSize, decimal Price, decimal Notional,
    DateTime DateTime, DateTime? ExpiresAt, DateTime? CancelledAt);

public sealed record OrderSummary(
    int Id, int InstrumentId, string Ticker, string Side, string Type, string Status,
    int Size, int FilledSize, decimal Price);

public sealed record PositionResult(
    int InstrumentId, string Ticker, string Name, int Quantity, int AvailableQuantity,
    decimal? Close, decimal? MarketValue, decimal? AverageCost,
    decimal? TotalReturnPercent, decimal? DailyReturnPercent);

public sealed record PortfolioResult(
    int UserId, decimal TotalAccountValue, decimal AvailableCash,
    decimal AccountingCash, decimal ReservedCash, IReadOnlyList<PositionResult> Positions);

public sealed record InstrumentResult(int Id, string Ticker, string Name, string Type);

public sealed record Paged<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public sealed record CancelResult(int Id, string Status, DateTime CancelledAt);
