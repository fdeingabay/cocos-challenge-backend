using Cocos.Domain.Entities;

namespace Cocos.Application.Features.Orders.SubmitOrder;

/// <summary>
/// Lectura de instrumentos y de su último precio conocido. Va aparte del lock de cuenta porque
/// no es estado del usuario: el instrumento y su cotizacion son iguales para todos y no forman
/// parte del invariante que el lock protege.
/// </summary>
public interface IInstrumentReader
{
    Task<InstrumentSnapshot?> FindAsync(int instrumentId, CancellationToken cancellationToken);

    /// <summary>
    /// Ultimo cierre conocido: el precio al que se valua una MARKET. Devuelve null si el
    /// instrumento todavia no tiene market data.
    /// </summary>
    Task<decimal?> GetLastCloseAsync(int instrumentId, CancellationToken cancellationToken);
}

/// <param name="Type">ACCIONES, MONEDA, etc. Se conserva el literal de la base tal cual.</param>
public sealed record InstrumentSnapshot(int Id, string Ticker, string Type)
{
    /// <summary>
    /// El ARS esta modelado como instrumento MONEDA y no se opera con órdenes al mercado: el
    /// cash se mueve con CASH_IN / CASH_OUT.
    /// </summary>
    public bool IsCurrency => Type == Instrument.CurrencyType;
}
