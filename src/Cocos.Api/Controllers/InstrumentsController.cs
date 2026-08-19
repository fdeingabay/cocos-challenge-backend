using Cocos.Api.Infrastructure;
using Cocos.Application.Common;
using Cocos.Application.Features.Instruments.SearchInstruments;
using Microsoft.AspNetCore.Mvc;
using Wolverine;

namespace Cocos.Api.Controllers;

[ApiController]
[Route("api/instruments")]
[Produces("application/json")]
public sealed class InstrumentsController(IMessageBus bus) : ControllerBase
{
    /// <summary>
    /// Busca activos por ticker o por nombre. Sin termino de busqueda devuelve el listado
    /// completo paginado.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<PagedResult<InstrumentResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<InstrumentResponse>>> Search(
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = Paging.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var result = await bus.InvokeAsync<Result<PagedResult<InstrumentResponse>>>(
            new SearchInstrumentsQuery(search, page, pageSize), cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : result.Error.ToProblem();
    }
}
