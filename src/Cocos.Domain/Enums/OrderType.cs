namespace Cocos.Domain.Enums;

public enum OrderType
{
    /// <summary>Se ejecuta en el acto contra el último precio del mercado.</summary>
    Market,

    /// <summary>Queda viva en el libro al precio pedido, hasta el cierre de la jornada.</summary>
    Limit
}
