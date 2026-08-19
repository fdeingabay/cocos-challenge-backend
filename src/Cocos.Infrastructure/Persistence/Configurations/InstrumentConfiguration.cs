using Cocos.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cocos.Infrastructure.Persistence.Configurations;

public sealed class InstrumentConfiguration : IEntityTypeConfiguration<Instrument>
{
    public void Configure(EntityTypeBuilder<Instrument> builder)
    {
        builder.ToTable("instruments");
        builder.HasKey(i => i.Id);

        builder.Property(i => i.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(i => i.Ticker).HasColumnName("ticker").HasMaxLength(10);
        builder.Property(i => i.Name).HasColumnName("name").HasMaxLength(255);
        builder.Property(i => i.Type).HasColumnName("type").HasMaxLength(10);
    }
}
