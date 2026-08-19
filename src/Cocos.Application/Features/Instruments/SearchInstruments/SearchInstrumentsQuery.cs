using Cocos.Application.Common;

namespace Cocos.Application.Features.Instruments.SearchInstruments;

public sealed record SearchInstrumentsQuery(string? Search, int Page = 1, int PageSize = Paging.DefaultPageSize);

public sealed record InstrumentResponse(int Id, string Ticker, string Name, string Type);
