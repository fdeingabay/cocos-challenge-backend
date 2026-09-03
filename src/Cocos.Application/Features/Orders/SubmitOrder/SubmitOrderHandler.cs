using Cocos.Application.Common;
using Cocos.Domain;
using Cocos.Domain.Entities;
using Cocos.Domain.Enums;
using FluentValidation;

namespace Cocos.Application.Features.Orders.SubmitOrder;

/// <summary>
/// Envio de una orden al mercado.
///
/// Todo el caso de uso ocurre bajo el lock de cuenta: evaluar el disponible e insertar la orden
/// tienen que ser una sola operación indivisible, o dos requests concurrentes del mismo usuario
/// gastan el mismo peso dos veces. El lock dura lo que dura el IAccountLock, y como todas las
/// lecturas de estado cuelgan de el, consultar el disponible por fuera no es posible.
/// </summary>
public sealed class SubmitOrderHandler(
    IAccountLedger accounts,
    IInstrumentReader instruments,
    IValidator<SubmitOrderCommand> validator,
    TimeProvider timeProvider)
{
    public async Task<Result<SubmitOrderOutcome>> Handle(
        SubmitOrderCommand command,
        CancellationToken cancellationToken)
    {
        var validation = await validator.ValidateAsync(command, cancellationToken);
        if (!validation.IsValid)
            return Error.Validation("order.invalid",
                string.Join(" ", validation.Errors.Select(e => e.ErrorMessage)));

        await using var account = await accounts.LockAsync(command.UserId, cancellationToken);
        if (account is null)
            return Error.NotFound("user.not_found", $"No existe el usuario {command.UserId}.");

        // Dentro del lock: dos reintentos simultaneos se serializan aca, y el segundo ve la
        // orden que creo el primero en vez de insertar una duplicada.
        var idempotencyKey = IdempotencyKey.Normalize(command.IdempotencyKey);
        if (await account.FindOrderByKeyAsync(idempotencyKey, cancellationToken) is { } alreadyPlaced)
            return SubmitOrderOutcome.Replayed(alreadyPlaced);

        var instrument = await instruments.FindAsync(command.InstrumentId, cancellationToken);
        if (instrument is null)
            return Error.NotFound("instrument.not_found",
                $"No existe el instrumento {command.InstrumentId}.");

        if (instrument.IsCurrency)
            return Error.Validation("instrument.not_tradable",
                "El instrumento es una moneda; el cash se mueve con CASH_IN / CASH_OUT, no con órdenes de mercado.");

        var price = await ResolvePriceAsync(command, instrument, cancellationToken);
        if (price.IsFailure) return price.Error;

        var request = OrderRequest.For(
            command.UserId, command.InstrumentId, command.Side, command.Type,
            command.Size, command.Amount, price.Value, idempotencyKey);

        if (!request.HasTradeableSize)
            return Error.Validation("order.size_zero",
                $"El monto enviado no alcanza para comprar ni una accion a {price.Value:0.00}. No se persiste la orden porque no llega a formarse.");

        var availability = await account.GetAvailabilityAsync(request, cancellationToken);
        var order = Order.Place(request, availability.CanSupport(request), timeProvider.GetUtcNow().UtcDateTime);

        account.Place(order);
        await account.CommitAsync();

        return SubmitOrderOutcome.Placed(SubmitOrderResponse.For(order, instrument.Ticker));
    }

    /// <summary>
    /// Una LIMIT trae su precio; una MARKET se valua contra el último cierre conocido.
    /// </summary>
    private async Task<Result<decimal>> ResolvePriceAsync(
        SubmitOrderCommand command,
        InstrumentSnapshot instrument,
        CancellationToken cancellationToken)
    {
        if (command.Type == OrderType.Limit) return command.Price!.Value;

        var close = await instruments.GetLastCloseAsync(command.InstrumentId, cancellationToken);

        return close is null or <= 0m
            ? Error.Validation("instrument.no_market_price",
                $"El instrumento {instrument.Ticker} no tiene precio de mercado disponible.")
            : close.Value;
    }
}
