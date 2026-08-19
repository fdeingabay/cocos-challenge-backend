using Cocos.Api.Infrastructure;
using Cocos.Application.Common;
using Cocos.Application.Features.Orders.GetUserOrders;
using Cocos.Application.Features.Portfolio.GetPortfolio;
using Microsoft.AspNetCore.Mvc;
using Wolverine;

namespace Cocos.Api.Controllers;

[ApiController]
[Route("api/users")]
[Produces("application/json")]
public sealed class UsersController(IMessageBus bus) : ControllerBase
{
    /// <summary>
    /// Portfolio del usuario: valor total de la cuenta, pesos disponibles para operar y
    /// posiciones con cantidad, valor de mercado y rendimiento.
    /// </summary>
    [HttpGet("{userId:int}/portfolio")]
    [ProducesResponseType<PortfolioResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PortfolioResponse>> GetPortfolio(
        int userId,
        CancellationToken cancellationToken)
    {
        var result = await bus.InvokeAsync<Result<PortfolioResponse>>(
            new GetPortfolioQuery(userId), cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : result.Error.ToProblem();
    }

    /// <summary>
    /// Ordenes del usuario, paginadas. Filtrar por status NEW para saber cuales se pueden cancelar.
    /// </summary>
    [HttpGet("{userId:int}/orders")]
    [ProducesResponseType<PagedResult<OrderSummaryResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<OrderSummaryResponse>>> GetOrders(
        int userId,
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = Paging.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        var result = await bus.InvokeAsync<Result<PagedResult<OrderSummaryResponse>>>(
            new GetUserOrdersQuery(userId, status, page, pageSize), cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : result.Error.ToProblem();
    }
}
