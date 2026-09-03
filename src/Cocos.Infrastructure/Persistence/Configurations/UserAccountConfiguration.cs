using Cocos.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Cocos.Infrastructure.Persistence.Configurations;

public sealed class UserAccountConfiguration : IEntityTypeConfiguration<UserAccount>
{
    public void Configure(EntityTypeBuilder<UserAccount> builder)
    {
        builder.ToTable("user_accounts");
        builder.HasKey(a => a.UserId);
        // ValueGeneratedNever: esta tabla no genera el userid, ya existe en las órdenes. Solo
        // lo replica para tener una fila por usuario que bloquear.
        builder.Property(a => a.UserId).HasColumnName("userid").ValueGeneratedNever();
    }
}
