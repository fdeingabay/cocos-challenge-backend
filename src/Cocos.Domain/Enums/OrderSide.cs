namespace Cocos.Domain.Enums;

public enum OrderSide
{
    Buy,
    Sell,

    /// <summary>Deposito de pesos. Es un movimiento de fondos, no una orden al mercado.</summary>
    CashIn,

    /// <summary>Retiro de pesos. Es un movimiento de fondos, no una orden al mercado.</summary>
    CashOut
}
