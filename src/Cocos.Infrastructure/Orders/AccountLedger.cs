using Cocos.Application.Common;
using Cocos.Application.Features.Orders.SubmitOrder;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Cocos.Infrastructure.Orders;

/// <summary>
/// Toma el lock de una cuenta abriendo la transacción que lo sostiene.
///
/// Recibe el ICocosDbContext scoped -- el MISMO que usa el handler -- y no un
/// IDbConnectionFactory. En Postgres la transacción es una propiedad de la sesion: otra
/// conexión es otra sesion, no ve el lock y lee el disponible fuera de el. Compila igual y el
/// invariante se rompe en silencio.
/// </summary>
public sealed class AccountLedger(ICocosDbContext db) : IAccountLedger
{
    public async Task<IAccountLock?> LockAsync(int userId, CancellationToken cancellationToken)
    {
        var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Entre el BeginTransaction y el AccountLock la transacción no tiene dueño: todavia no
        // se devolvio, asi que el "await using" del handler no la alcanza. Si el SELECT ... FOR
        // UPDATE falla -- el cliente corta esperando el lock de otra transacción, se cae la
        // conexión -- nadie la dispondria y quedaria abierta sobre el DbContext scoped, quiza ya
        // reteniendo la fila, hasta que muera el scope.
        try
        {
            var exists = await db.Database.GetDbConnection().ExecuteScalarAsync<int?>(new CommandDefinition(
                AccountSql.LockAccount,
                new { UserId = userId },
                transaction.GetDbTransaction(),
                cancellationToken: cancellationToken));

            if (exists is not null)
                return new AccountLock(db, transaction, userId);
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }

        // El usuario no existe: no hay cuenta que serializar ni nada que commitear.
        await transaction.DisposeAsync();
        return null;
    }
}
