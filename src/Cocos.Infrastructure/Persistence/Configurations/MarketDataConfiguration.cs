using Cocos.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cocos.Infrastructure.Persistence.Configurations;

public sealed class MarketDataConfiguration : IEntityTypeConfiguration<MarketData>
{
    public void Configure(EntityTypeBuilder<MarketData> builder)
    {
        builder.ToTable("marketdata");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(m => m.InstrumentId).HasColumnName("instrumentid");
        builder.Property(m => m.High).HasColumnName("high").HasColumnType("numeric(10,2)");
        builder.Property(m => m.Low).HasColumnName("low").HasColumnType("numeric(10,2)");
        builder.Property(m => m.Open).HasColumnName("open").HasColumnType("numeric(10,2)");
        builder.Property(m => m.Close).HasColumnName("close").HasColumnType("numeric(10,2)");
        builder.Property(m => m.PreviousClose).HasColumnName("previousclose").HasColumnType("numeric(10,2)");
        builder.Property(m => m.Date).HasColumnName("date");

        // Descendente por fecha: toda consulta de precio pide el último close, o sea el primer
        // renglon de este indice. Asi lo resuelve sin ordenar.
        builder.HasIndex(m => new { m.InstrumentId, m.Date })
               .HasDatabaseName("ix_marketdata_instrument_date")
               .IsDescending(false, true);
    }
}
