using Cocos.Domain;
using Cocos.Domain.Entities;
using Cocos.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Cocos.Infrastructure.Persistence.Configurations;

public sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    /// <summary>
    /// Postgres pliega a minusculas todo identificador que no venga entre comillas, y el
    /// DDL provisto los declara sin comillas. Las columnas reales son "instrumentid",
    /// "previousclose", etc. Por eso cada columna se mapea explicitamente: confiar en la
    /// convencion PascalCase de EF produce errores de "column does not exist" en runtime.
    /// </summary>
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("orders");
        builder.HasKey(o => o.Id);

        builder.Property(o => o.Id).HasColumnName("id").ValueGeneratedOnAdd();
        builder.Property(o => o.InstrumentId).HasColumnName("instrumentid").IsRequired();
        builder.Property(o => o.UserId).HasColumnName("userid").IsRequired();
        builder.Property(o => o.Size).HasColumnName("size").IsRequired();
        builder.Property(o => o.FilledSize).HasColumnName("filledsize").IsRequired();
        builder.Property(o => o.Price).HasColumnName("price").HasColumnType("numeric(10,2)").IsRequired();

        builder.Property(o => o.Type)
            .HasColumnName("type")
            .HasConversion(v => v.ToDb(), v => DbValues.ToOrderType(v))
            .HasMaxLength(10)
            .IsRequired();

        builder.Property(o => o.Side)
            .HasColumnName("side")
            .HasConversion(v => v.ToDb(), v => DbValues.ToOrderSide(v))
            .HasMaxLength(10)
            .IsRequired();

        builder.Property(o => o.Status)
            .HasColumnName("status")
            .HasConversion(v => v.ToDb(), v => DbValues.ToOrderStatus(v))
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(o => o.DateTime)
            .HasColumnName("datetime")
            // El tipo va explicito: el default de Npgsql para DateTime es
            // 'timestamp with time zone', que rechaza un Kind=Unspecified. El esquema
            // provisto usa TIMESTAMP sin zona, asi que hay que decirlo.
            .HasColumnType("timestamp without time zone")
            .HasConversion(TimestampConverters.Unspecified)
            .IsRequired();

        builder.Property(o => o.ExpiresAt)
            .HasColumnName("expiresat")
            .HasColumnType("timestamp without time zone")
            .HasConversion(TimestampConverters.NullableUnspecified);

        builder.Property(o => o.IdempotencyKey).HasColumnName("idempotencykey");

        builder.HasIndex(o => new { o.UserId, o.Status }).HasDatabaseName("ix_orders_user_status");
    }
}

/// <summary>
/// Las columnas de tiempo del esquema provisto son TIMESTAMP sin timezone. Npgsql rechaza
/// un DateTime con Kind=Utc contra ese tipo. La convencion del proyecto es guardar UTC,
/// asi que se traduce el Kind en el borde: Unspecified al escribir, Utc al leer.
/// </summary>
internal static class TimestampConverters
{
    public static readonly ValueConverter<DateTime, DateTime> Unspecified = new(
        toDb => DateTime.SpecifyKind(toDb, DateTimeKind.Unspecified),
        fromDb => DateTime.SpecifyKind(fromDb, DateTimeKind.Utc));

    public static readonly ValueConverter<DateTime?, DateTime?> NullableUnspecified = new(
        toDb => toDb.HasValue ? DateTime.SpecifyKind(toDb.Value, DateTimeKind.Unspecified) : null,
        fromDb => fromDb.HasValue ? DateTime.SpecifyKind(fromDb.Value, DateTimeKind.Utc) : null);
}
