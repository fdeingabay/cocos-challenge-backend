namespace Cocos.Domain.Entities;

public sealed class Instrument
{
    public const string CurrencyType = "MONEDA";

    private Instrument() { } // EF Core

    public int Id { get; private set; }
    public string Ticker { get; private set; } = null!;
    public string Name { get; private set; } = null!;

    /// <summary>ACCIONES, MONEDA, etc. El cash (ARS) esta modelado como instrumento MONEDA.</summary>
    public string Type { get; private set; } = null!;

    public bool IsCurrency => Type == CurrencyType;
}
