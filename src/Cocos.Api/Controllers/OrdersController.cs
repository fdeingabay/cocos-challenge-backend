using Cocos.Api.Infrastructure;
using Cocos.Application.Common;
using Cocos.Application.Features.Orders.CancelOrder;
using Cocos.Application.Features.Orders.GetOrder;
using Cocos.Application.Features.Orders.SubmitOrder;
using Cocos.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Wolverine;

namespace Cocos.Api.Controllers;

[ApiController]
[Route("api/orders")]
[Produces("application/json")]
public sealed class OrdersController(IMessageBus bus) : ControllerBase
{
    /// <summary>
    /// Envia una orden de compra o venta al mercado.
    /// </summary>
    /// <remarks>
    /// Las MARKET se ejecutan al instante contra el último precio y quedan en FILLED. Las LIMIT
    /// quedan en NEW reservando pesos (compra) o nominales (venta) hasta ejecutarse, cancelarse
    /// o vencer al cierre de la jornada.
    ///
    /// Una orden sin fondos o sin tenencia suficiente no es un error: se persiste en estado
    /// REJECTED y se devuelve 201, porque la solicitud se proceso bien.
    /// </remarks>
    /// <param name="request">Datos de la orden. Enviar 'size' o 'amount', nunca los dos.</param>
    /// <param name="idempotencyKey">
    /// Opcional pero recomendado. Sin ella, una perdida de senal o un doble toque crean DOS
    /// órdenes y el usuario compra dos veces.
    ///
    /// Que enviar: un valor opaco y aleatorio, por ejemplo un UUID v4. No derivarlo del
    /// contenido de la orden: comprar dos veces lo mismo es una operación legitima, y un hash
    /// del contenido se comeria la segunda en silencio. La clave identifica el INTENTO.
    ///
    /// Cuando generarla: una sola vez, cuando el usuario confirma la operación, y reusar ese
    /// mismo valor en cada reintento de esa intención. Una clave nueva por request HTTP no
    /// protege de nada. Conviene persistirla junto a la orden pendiente: si la app se cierra
    /// durante el timeout y al reabrir manda otra clave, se duplica igual.
    ///
    /// Un reintento responde 200 con la orden original, en lugar del 201 del alta.
    ///
    /// La clave es única por usuario, asi que dos usuarios pueden usar el mismo valor sin
    /// interferir. Limite de 128 caracteres.
    /// </param>
    /// <param name="cancellationToken">Token de cancelación de la solicitud.</param>
    [HttpPost]
    [ProducesResponseType<SubmitOrderResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<SubmitOrderResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SubmitOrderResponse>> Submit(
        [FromBody] SubmitOrderRequest request,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var command = new SubmitOrderCommand(
            request.UserId, request.InstrumentId, request.Side, request.Type,
            request.Size, request.Amount, request.Price, idempotencyKey);

        var result = await bus.InvokeAsync<Result<SubmitOrderOutcome>>(command, cancellationToken);

        if (result.IsFailure)
            return result.Error.ToProblem();

        // Un reintento no creo nada, asi que no puede contestar 201: el cliente que mire el
        // código para decidir si registrar un alta contaria dos veces la misma compra. El
        // handler distingue los dos desenlaces; aca solo se traducen a HTTP.
        var outcome = result.Value;

        if (outcome.IsReplay)
            return Ok(outcome.Order);

        return CreatedAtAction(
            nameof(GetById),
            new { orderId = outcome.Order.Id, userId = outcome.Order.UserId },
            outcome.Order);
    }

    /// <summary>
    /// Una orden puntual del usuario. Es el recurso al que apunta el Location del alta.
    /// </summary>
    /// <remarks>
    /// Una orden de otro usuario devuelve 404 y no 403: contestar distinto le confirmaria que
    /// esa orden existe para alguien mas.
    /// </remarks>
    [HttpGet("{orderId:int}", Name = nameof(GetById))]
    [ProducesResponseType<GetOrderResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<GetOrderResponse>> GetById(
        int orderId,
        [FromQuery, BindRequired] int userId,
        CancellationToken cancellationToken)
    {
        var result = await bus.InvokeAsync<Result<GetOrderResponse>>(
            new GetOrderQuery(orderId, userId), cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : result.Error.ToProblem();
    }

    /// <summary>
    /// Cancela una orden viva. Solo aplica a órdenes en estado NEW o PARTIALLY_FILLED.
    /// Al dejar de estar viva, la orden deja de reservar y el disponible se recupera solo.
    /// </summary>
    [HttpPost("{orderId:int}/cancel")]
    [ProducesResponseType<CancelOrderResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CancelOrderResponse>> Cancel(
        int orderId,
        // BindRequired y no un int a secas: sin el, un userId ausente se bindea a 0 y el caso
        // de uso contesta "no existe la orden N para el usuario 0" -- un 404 que miente, porque
        // la orden si existe. Falta un parámetro, y eso es 400.
        [FromQuery, BindRequired] int userId,
        CancellationToken cancellationToken)
    {
        var result = await bus.InvokeAsync<Result<CancelOrderResponse>>(
            new CancelOrderCommand(orderId, userId), cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : result.Error.ToProblem();
    }
}

/// <param name="UserId">Usuario que envía la orden.</param>
/// <param name="InstrumentId">Instrumento a operar.</param>
/// <param name="Side">BUY o SELL.</param>
/// <param name="Type">MARKET o LIMIT.</param>
/// <param name="Size">Cantidad exacta de acciones. Excluyente con 'amount'.</param>
/// <param name="Amount">Monto en pesos a invertir; se traduce a la cantidad máxima de acciones enteras. Excluyente con 'size'.</param>
/// <param name="Price">Precio limite. Obligatorio para LIMIT, ignorado para MARKET.</param>
public sealed record SubmitOrderRequest(
    int UserId,
    int InstrumentId,
    OrderSide Side,
    OrderType Type,
    int? Size,
    decimal? Amount,
    decimal? Price);
