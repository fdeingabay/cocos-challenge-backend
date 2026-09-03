using Cocos.Application.Common;
using Cocos.Application.Features.Orders.SubmitOrder;
using Cocos.Domain;
using Cocos.Domain.Entities;
using Cocos.Domain.Enums;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Cocos.Infrastructure.Orders;

/// <summary>
/// La cuenta tomada en exclusiva. Su vida ES la vida del lock: mientras exista este objeto la
/// cuenta esta serializada. Toda consulta va por la conexión del DbContext y lleva la
/// transacción explícita, que es la única forma de que corra dentro del lock.
/// </summary>
public sealed class AccountLock(ICocosDbContext db, IDbContextTransaction transaction, int userId)
    : IAccountLock
{
    public async Task<SubmitOrderResponse?> FindOrderByKeyAsync(
        string? idempotencyKey, CancellationToken cancellationToken)
    {
        if (idempotencyKey is null) return null;

        var existingId = await QueryScalarAsync<int?>(
            AccountSql.FindByIdempotencyKey,
            new { UserId = userId, Key = idempotencyKey },
            cancellationToken);

        if (existingId is null) return null;

        var row = await db.Database.GetDbConnection().QuerySingleAsync<OrderRow>(new CommandDefinition(
            AccountSql.OrderById, new { Id = existingId.Value },
            transaction.GetDbTransaction(), cancellationToken: cancellationToken));

        return row.ToResponse();
    }

    public async Task<AccountAvailability> GetAvailabilityAsync(
        OrderRequest request, CancellationToken cancellationToken)
        // Cada orden consume un solo recurso, asi que se agrega uno solo: la consulta que no
        // hace falta ni se ejecuta, y el lock se suelta antes.
        => request.Side == OrderSide.Buy
            ? AccountAvailability.ForBuy(await QueryScalarAsync<decimal>(
                AccountSql.AvailableCash,
                new { UserId = userId, DbValues.OpenStatuses },
                cancellationToken))
            : AccountAvailability.ForSell(await QueryScalarAsync<int>(
                AccountSql.AvailableQuantity,
                new { UserId = userId, request.InstrumentId, DbValues.OpenStatuses },
                cancellationToken));

    public void Place(Order order) => db.Orders.Add(order);

    public async Task CommitAsync()
    {
        // Sin CancellationToken a propósito: que el cliente corte la conexión no puede dejar
        // una orden aplicada a medias. El commit se termina siempre.
        await db.SaveChangesAsync(CancellationToken.None);
        await transaction.CommitAsync(CancellationToken.None);
    }

    /// <summary>
    /// Libera el lock. Si nadie commiteo -- el caso de uso corto por un 404, un 400 o una
    /// excepcion -- la transacción se revierte y no queda nada aplicado a medias.
    ///
    /// Sin RollbackAsync explicito a propósito: disponer una transacción no commiteada ya la
    /// revierte, y pedirlo a mano solo agrega un modo de falla. Si el commit falló porque se
    /// cayo la conexión, el rollback falla también, y desde adentro del "await using" su
    /// excepción REEMPLAZA a la original: en el log queda el síntoma y se pierde la causa.
    /// </summary>
    public ValueTask DisposeAsync() => transaction.DisposeAsync();

    private Task<T?> QueryScalarAsync<T>(string sql, object parameters, CancellationToken cancellationToken)
        => db.Database.GetDbConnection().ExecuteScalarAsync<T?>(new CommandDefinition(
            sql, parameters, transaction.GetDbTransaction(), cancellationToken: cancellationToken));
}
