using Cocos.Application.Common;
using Cocos.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cocos.Infrastructure.Persistence;

/// <summary>
/// DbContext = Unit of Work. No se agrega un Repository ni un UoW propio encima:
/// serian una capa de indireccion sobre una abstraccion que ya existe, y ademas
/// esconderian justamente lo que aca importa (transacciones, locks explicitos).
/// </summary>
public sealed class CocosDbContext(DbContextOptions<CocosDbContext> options)
    : DbContext(options), ICocosDbContext
{
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Instrument> Instruments => Set<Instrument>();
    public DbSet<MarketData> MarketData => Set<MarketData>();
    public DbSet<UserAccount> UserAccounts => Set<UserAccount>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ApplyConfigurationsFromAssembly(typeof(CocosDbContext).Assembly);
}
