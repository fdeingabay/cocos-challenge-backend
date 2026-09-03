using Cocos.Application.Common;

namespace Cocos.Application.Features.Orders.GetUserOrders;

/// <param name="Status">Literal de la base ("FILLED", "NEW", ...). Ausente significa sin filtro.</param>
public sealed record GetUserOrdersQuery(
    int UserId,
    string? Status = null,
    int Page = 1,
    int PageSize = Paging.DefaultPageSize);

public sealed record OrderSummaryResponse(
    int Id,
    int InstrumentId,
    string Ticker,
    string Side,
    string Type,
    string Status,
    int Size,
    int FilledSize,
    decimal Price,
    DateTime DateTime,
    DateTime? ExpiresAt);
