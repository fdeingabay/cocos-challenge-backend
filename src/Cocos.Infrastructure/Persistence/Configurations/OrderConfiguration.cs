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
    /// Postgres pliega a minúsculas todo identificador que no venga entre comillas, y el DDL
    /// provisto los declara sin comillas: las columnas reales son "instrumentid",
    /// "previousclose". Por eso cada una se mapea con HasColumnName explicito; confiar en la
    /// convención PascalCase de EF falla recién en runtime, con "column does not exist".
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
            // El tipo va explicito: el default de Npgsql para DateTime es 'timestamp with time
            // zone' y el esquema provisto usa TIMESTAMP sin zona. Sin decirlo, escribir lanza
            // ArgumentException.
            .HasColumnType("timestamp without time zone")
            .HasConversion(TimestampConverters.Unspecified)
            .IsRequired();

        builder.Property(o => o.ExpiresAt)
            .HasColumnName("expiresat")
            .HasColumnType("timestamp without time zone")
            .HasConversion(TimestampConverters.NullableUnspecified);

        builder.Property(o => o.CancelledAt)
            .HasColumnName("cancelledat")
            .HasColumnType("timestamp without time zone")
            .HasConversion(TimestampConverters.NullableUnspecified);

        builder.Property(o => o.IdempotencyKey).HasColumnName("idempotencykey");

        builder.HasIndex(o => new { o.UserId, o.Status }).HasDatabaseName("ix_orders_user_status");
    }
}

/// <summary>
/// Las columnas de tiempo del esquema provisto son TIMESTAMP sin zona y Npgsql rechaza un
/// DateTime con Kind=Utc contra ese tipo. Como la convencion del proyecto es guardar UTC, el
/// Kind se traduce en el borde: Unspecified al escribir, Utc al leer.
/// </summary>
internal static class TimestampConverters
{
    public static readonly ValueConverter<DateTime, DateTime> Unspecified = new(
        toDb => DateTime.SpecifyKind(toDb, DateTimeKind.Unspecified),
        fromDb => DateTime.SpecifyKind(fromDb, DateTimeKind.Utc));

    public static readonly ValueConverter<DateTime?, DateTime?> NullableUnspecified = new(
        toDb => toDb.HasValue ? DateTime.SpecifyKind(toDb.Value, DateTimeKind.Unspecified) : null,
        fromDb => fromDb.HasValue ? DateTime.SpecifyKind(fromDb.Value, DateTimeKind.Utc) : null);

    /// <summary>
    /// La misma traduccion para el SQL escrito a mano, porque Dapper no pasa por los converters
    /// de EF. Sin esto Npgsql infiere timestamptz por el Kind=Utc y Postgres reinterpreta el
    /// valor segun el TimeZone de la SESION al compararlo contra una columna sin zona: correcto
    /// mientras el server este en UTC, corrido en silencio si no.
    /// </summary>
    public static DateTime ToDb(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
}
