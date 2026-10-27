using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Persistence.Entities;
using AlmoSeis.Infrastructure.Tenancy;
using AlmoSeis.Modules.Identity.Domain;
using Microsoft.EntityFrameworkCore;

namespace AlmoSeis.Modules.Identity.Infrastructure;

/// Mapeamento de 05 §5.3. Nomes de coluna vêm da convenção snake_case.
internal sealed class IdentityModelConfiguration : IModuleModelConfiguration
{
    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(b =>
        {
            b.ToTable("users");
            b.HasKey(x => x.Id);
            b.Property(x => x.FullName).HasMaxLength(120);
            b.Property(x => x.Email).HasMaxLength(254);
            b.Property(x => x.NormalizedEmail).HasMaxLength(254);
            b.Property(x => x.PhoneNumber).HasMaxLength(16);
            b.Property(x => x.SecurityStamp).HasMaxLength(64);
            b.Property(x => x.ConcurrencyStamp).HasMaxLength(64);
        });

        modelBuilder.Entity<Tenant>(b =>
        {
            b.ToTable("tenants");
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(120);
            b.Property(x => x.DocumentNumber).HasMaxLength(14);
            b.Property(x => x.City).HasMaxLength(80);
            b.Property(x => x.State).HasMaxLength(2).IsFixedLength();
            b.Property(x => x.TimeZone).HasMaxLength(64);
            b.Property(x => x.TermsVersion).HasMaxLength(20);
            b.HasOne<User>().WithMany().HasForeignKey(x => x.TermsAcceptedByUserId); // ordena o INSERT: usuário antes
        });

        modelBuilder.Entity<TenantSettings>(b =>
        {
            b.ToTable("tenant_settings");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).HasColumnName("tenant_id");
            b.Ignore(x => x.TenantId);
            b.Property(x => x.KiloPricePerKg).HasPrecision(10, 2);
            b.Property(x => x.EnabledPaymentMethods).HasColumnType("text[]");
        });

        modelBuilder.Entity<TenantEmployee>(b =>
        {
            b.ToTable("tenant_employees");
            b.HasKey(x => x.Id);
            b.Property(x => x.DisplayName).HasMaxLength(60);
            b.Ignore(x => x.IsActive);
            b.Ignore(x => x.RoleValues);
            b.HasMany(x => x.Roles).WithOne().HasForeignKey(r => r.TenantEmployeeId);
            b.Navigation(x => x.Roles).UsePropertyAccessMode(PropertyAccessMode.Field);
            b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
        });

        modelBuilder.Entity<TenantEmployeeRole>(b =>
        {
            b.ToTable("tenant_employee_roles");
            b.HasKey(x => new { x.TenantEmployeeId, x.Role });
        });

        modelBuilder.Entity<RefreshToken>(b =>
        {
            b.ToTable("refresh_tokens");
            b.HasKey(x => x.Id);
            b.Property(x => x.InstallationId).HasMaxLength(64);
            b.Property(x => x.TokenHash).HasMaxLength(64).IsFixedLength();
            // Relações só para o EF ordenar os INSERTs; as FKs reais (compostas) estão no DDL.
            b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
            b.HasOne<TenantEmployee>().WithMany().HasForeignKey(x => x.TenantEmployeeId);
            b.HasOne<RefreshToken>().WithMany().HasForeignKey(x => x.ReplacedByTokenId); // INSERT do novo antes do UPDATE do antigo
        });

        modelBuilder.Entity<EmployeeInvitation>(b =>
        {
            b.ToTable("employee_invitations");
            b.HasKey(x => x.Id);
            b.Property(x => x.Code).HasMaxLength(6).IsFixedLength();
            b.Property(x => x.Roles).HasColumnType("text[]");
            b.Ignore(x => x.RoleValues);
            b.HasOne<User>().WithMany().HasForeignKey(x => x.AcceptedByUserId); // aceite com conta nova: usuário antes do UPDATE
        });

        modelBuilder.Entity<TenantSyncCounter>().HasOne<Tenant>().WithOne().HasForeignKey<TenantSyncCounter>(x => x.TenantId);
    }
}

/// TenantSettingsChanged → `CatalogUpdated` com `entityType: TENANT_SETTINGS` (06 §6.14).
internal sealed class TenantSettingsChangedMapper(ITenantContext tenant) : IntegrationEventMapper<TenantSettingsChanged>
{
    protected override IEnumerable<IntegrationEnvelope> Map(TenantSettingsChanged e)
    {
        yield return new IntegrationEnvelope("CatalogUpdated", "TENANT_SETTINGS", e.TenantId, tenant.EmployeeId, new { entityType = "TENANT_SETTINGS" });
    }
}
