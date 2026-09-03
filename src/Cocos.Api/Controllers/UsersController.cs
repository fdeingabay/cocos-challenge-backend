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
    ///
    /// El disponible ya viene neto de reserva; el contable y lo reservado se informan aparte.
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
    /// Ordenes del usuario, paginadas. Filtrar por status NEW para ver cuáles se pueden cancelar.
    /// </summary>
    /// <remarks>
    /// Un status que no existe devuelve 400 y no una lista vacia: "no hay órdenes en ese estado"
    /// y "ese estado no existe" son respuestas distintas, y confundirlas hace pasar un error del
    /// cliente por un resultado.
    /// </remarks>
    [HttpGet("{userId:int}/orders")]
    [ProducesResponseType<PagedResult<OrderSummaryResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
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
