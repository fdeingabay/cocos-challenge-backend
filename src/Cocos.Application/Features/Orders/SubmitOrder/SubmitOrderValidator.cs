using Cocos.Domain.Enums;
using FluentValidation;

namespace Cocos.Application.Features.Orders.SubmitOrder;

/// <summary>
/// Valida la FORMA del pedido. Todo lo que necesita conocer el precio o el saldo
/// (cantidad resultante, fondos suficientes) se resuelve en el handler, dentro de la
/// transaccion: fuera de ella el dato ya podria estar desactualizado.
/// </summary>
public sealed class SubmitOrderValidator : AbstractValidator<SubmitOrderCommand>
{
    public SubmitOrderValidator()
    {
        RuleFor(x => x.UserId).GreaterThan(0);
        RuleFor(x => x.InstrumentId).GreaterThan(0);

        // CASH_IN y CASH_OUT son transferencias, no ordenes de mercado: no se envian por aca.
        RuleFor(x => x.Side)
            .Must(side => side is OrderSide.Buy or OrderSide.Sell)
            .WithMessage("El side debe ser BUY o SELL.");

        RuleFor(x => x)
            .Must(x => x.Size.HasValue ^ x.Amount.HasValue)
            .WithMessage("Enviar exactamente uno de los dos: 'size' (cantidad de acciones) o 'amount' (monto en pesos).");

        RuleFor(x => x.Size)
            .GreaterThan(0).When(x => x.Size.HasValue)
            .WithMessage("La cantidad debe ser mayor a cero.");

        RuleFor(x => x.Amount)
            .GreaterThan(0m).When(x => x.Amount.HasValue)
            .WithMessage("El monto debe ser mayor a cero.");

        RuleFor(x => x.Price)
            .NotNull().GreaterThan(0m)
            .When(x => x.Type == OrderType.Limit)
            .WithMessage("Una orden LIMIT requiere un precio mayor a cero.");

        // Cota superior defensiva: la columna es numeric(10,2), un valor mayor
        // reventaria en la base con un error opaco en vez de un 400 claro (OWASP API4).
        RuleFor(x => x.Price)
            .LessThan(100_000_000m).When(x => x.Price.HasValue)
            .WithMessage("El precio excede el maximo admitido.");

        RuleFor(x => x.IdempotencyKey)
            .MaximumLength(128).When(x => x.IdempotencyKey is not null);
    }
}
