using Cocos.Application.Common;
using Cocos.Application.Features.Orders.SubmitOrder;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Cocos.Infrastructure.Orders;

/// <summary>
/// Instrumentos y market data. Usa el mismo ICocosDbContext que el resto del caso de uso para
/// no pedirle una segunda conexión al pool mientras hay un lock de cuenta tomado: si hay
/// transacción viva se enlista en ella, y si no la hay lee igual.
/// </summary>
public sealed class InstrumentReader(ICocosDbContext db) : IInstrumentReader
{
    public Task<InstrumentSnapshot?> FindAsync(int instrumentId, CancellationToken cancellationToken)
        => db.Database.GetDbConnection().QuerySingleOrDefaultAsync<InstrumentSnapshot>(new CommandDefinition(
            AccountSql.Instrument, new { InstrumentId = instrumentId },
            CurrentTransaction, cancellationToken: cancellationToken));

    public Task<decimal?> GetLastCloseAsync(int instrumentId, CancellationToken cancellationToken)
        => db.Database.GetDbConnection().ExecuteScalarAsync<decimal?>(new CommandDefinition(
            AccountSql.LastClose, new { InstrumentId = instrumentId },
            CurrentTransaction, cancellationToken: cancellationToken));

    private System.Data.Common.DbTransaction? CurrentTransaction
        => db.Database.CurrentTransaction?.GetDbTransaction();
}
