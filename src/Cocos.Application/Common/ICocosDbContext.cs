using Cocos.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Cocos.Application.Common;

/// <summary>
/// El DbContext declarado en la capa que lo consume, para invertir la dependencia: Application
/// no puede referenciar Infrastructure.
/// </summary>
public interface ICocosDbContext
{
    DbSet<Order> Orders { get; }
    DatabaseFacade Database { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
