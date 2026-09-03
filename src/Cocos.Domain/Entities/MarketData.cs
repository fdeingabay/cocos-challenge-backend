namespace Cocos.Domain.Entities;

public sealed class MarketData
{
    private MarketData() { } // EF Core

    public int Id { get; private set; }
    public int InstrumentId { get; private set; }
    public decimal? High { get; private set; }
    public decimal? Low { get; private set; }
    public decimal? Open { get; private set; }

    /// <summary>Ultimo precio del activo. Es el que valua a las órdenes MARKET.</summary>
    public decimal? Close { get; private set; }

    public decimal? PreviousClose { get; private set; }

    /// <summary>
    /// El DDL provisto declara esta columna como DATE, aunque el enunciado la liste como
    /// datetime. Hay dos dias cargados por instrumento.
    /// </summary>
    public DateOnly Date { get; private set; }
}
