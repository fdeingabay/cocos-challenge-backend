using Cocos.Api.Infrastructure;
using Cocos.Application.Common;
using Cocos.Application.Features.Orders.CancelOrder;
using Cocos.Application.Features.Orders.SubmitOrder;
using Cocos.Domain.Enums;
using Microsoft.AspNetCore.Mvc;
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
    /// Las MARKET se ejecutan al instante contra el ultimo precio y quedan en FILLED.
    /// Las LIMIT quedan en NEW reservando fondos (compra) o nominales (venta) hasta
    /// ejecutarse, cancelarse o vencer al cierre de la jornada.
    ///
    /// Una orden sin fondos o sin tenencia suficiente NO es un error: se persiste en estado
    /// REJECTED y se devuelve 201, porque la solicitud se proceso correctamente.
    /// </remarks>
    /// <param name="request">Datos de la orden. Enviar 'size' o 'amount', nunca los dos.</param>
    /// <param name="idempotencyKey">
    /// Opcional pero recomendado. Protege contra el reintento del cliente: sin esta clave,
    /// una perdida de senal o un doble toque crean DOS ordenes y el usuario compra dos veces.
    ///
    /// Que enviar: un valor opaco y aleatorio, por ejemplo un UUID v4. No derivarlo del
    /// contenido de la orden (un hash de instrumento + cantidad + precio) porque comprar dos
    /// veces lo mismo es una operacion legitima, y un hash de contenido se comeria la segunda
    /// en silencio. La clave identifica el INTENTO, no el contenido.
    ///
    /// Cuando generarla: una sola vez, en el momento en que el usuario confirma la operacion,
    /// y reusar exactamente ese valor en cada reintento de esa misma intencion. Si el cliente
    /// genera una clave nueva por cada request HTTP, la proteccion no existe. Conviene ademas
    /// persistirla junto a la orden pendiente: si la app se cierra durante el timeout y al
    /// reabrir manda una clave nueva, se duplica igual.
    ///
    /// Respuesta ante un reintento: 200 con la orden original, en lugar del 201 del alta.
    ///
    /// Alcance: la clave es unica por usuario, asi que dos usuarios pueden usar el mismo valor
    /// sin interferir. Limite de 128 caracteres.
    /// </param>
    /// <param name="cancellationToken">Token de cancelacion de la solicitud.</param>
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

        var result = await bus.InvokeAsync<Result<SubmitOrderResponse>>(command, cancellationToken);

        if (result.IsFailure)
            return result.Error.ToProblem();

        return CreatedAtAction(nameof(Submit), new { id = result.Value.Id }, result.Value);
    }

    /// <summary>
    /// Cancela una orden viva. Solo aplica a ordenes en estado NEW o PARTIALLY_FILLED.
    /// </summary>
    [HttpPost("{orderId:int}/cancel")]
    [ProducesResponseType<CancelOrderResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CancelOrderResponse>> Cancel(
        int orderId,
        [FromQuery] int userId,
        CancellationToken cancellationToken)
    {
        var result = await bus.InvokeAsync<Result<CancelOrderResponse>>(
            new CancelOrderCommand(orderId, userId), cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : result.Error.ToProblem();
    }
}

/// <param name="UserId">Usuario que envia la orden.</param>
/// <param name="InstrumentId">Instrumento a operar.</param>
/// <param name="Side">BUY o SELL.</param>
/// <param name="Type">MARKET o LIMIT.</param>
/// <param name="Size">Cantidad exacta de acciones. Excluyente con 'amount'.</param>
/// <param name="Amount">Monto en pesos a invertir; se calcula la cantidad maxima de acciones enteras. Excluyente con 'size'.</param>
/// <param name="Price">Precio limite. Obligatorio para LIMIT, ignorado para MARKET.</param>
public sealed record SubmitOrderRequest(
    int UserId,
    int InstrumentId,
    OrderSide Side,
    OrderType Type,
    int? Size,
    decimal? Amount,
    decimal? Price);
