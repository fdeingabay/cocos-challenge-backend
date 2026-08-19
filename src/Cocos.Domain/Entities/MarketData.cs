namespace Cocos.Domain.Entities;

public sealed class MarketData
{
    private MarketData() { } // EF Core

    public int Id { get; private set; }
    public int InstrumentId { get; private set; }
    public decimal? High { get; private set; }
    public decimal? Low { get; private set; }
    public decimal? Open { get; private set; }

    /// <summary>Ultimo precio del activo. Es el que usan las ordenes MARKET.</summary>
    public decimal? Close { get; private set; }

    public decimal? PreviousClose { get; private set; }

    /// <summary>
    /// La tabla provista define esta columna como DATE (el enunciado la lista como
    /// "datetime", pero el DDL real dice otra cosa). Hay dos dias cargados por instrumento.
    /// </summary>
    public DateOnly Date { get; private set; }
}
