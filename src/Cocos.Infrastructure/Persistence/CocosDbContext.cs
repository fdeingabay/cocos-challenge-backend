using Cocos.Application.Common;
using Cocos.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Cocos.Infrastructure.Persistence;

/// <summary>
/// El DbContext ya es la unidad de trabajo. No hay Repository ni UoW propio encima: serian una
/// indirección sobre una abstraccion que ya existe, y esconderian justo lo que aca importa, que
/// son las transacciones y los locks explícitos.
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
