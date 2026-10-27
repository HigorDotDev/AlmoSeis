using AlmoSeis.SharedKernel.Domain;
using AlmoSeis.SharedKernel.Security;

namespace AlmoSeis.Modules.Identity.Domain;

/// Identidade global (sem tenant). Senha via IPasswordHasher do ASP.NET Core Identity (ADR-0027).
internal sealed class User : Entity
{
    public const int MaxFailedAttempts = 5;
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    private User()
    {
    }

    public string FullName { get; private set; } = null!;
    public string? Email { get; private set; }
    public string? NormalizedEmail { get; private set; }
    public bool EmailConfirmed { get; private set; }
    public string? PhoneNumber { get; private set; }
    public bool PhoneNumberConfirmed { get; private set; }
    public string PasswordHash { get; private set; } = null!;
    public string SecurityStamp { get; private set; } = null!;
    public string ConcurrencyStamp { get; private set; } = null!;
    public bool LockoutEnabled { get; private set; } = true;
    public DateTimeOffset? LockoutEnd { get; private set; }
    public int AccessFailedCount { get; private set; }
    public bool IsPlatformAdmin { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static User Create(Guid id, string fullName, string? email, string? phoneNumber, DateTimeOffset now) => new()
    {
        Id = id,
        FullName = fullName.Trim(),
        Email = email?.Trim(),
        NormalizedEmail = NormalizeEmail(email),
        PhoneNumber = phoneNumber,
        SecurityStamp = Guid.NewGuid().ToString("N"),
        ConcurrencyStamp = Guid.NewGuid().ToString("N"),
        CreatedAt = now,
    };

    public static string? NormalizeEmail(string? email) => email?.Trim().ToUpperInvariant();

    public void SetPasswordHash(string hash)
    {
        PasswordHash = hash;
        SecurityStamp = Guid.NewGuid().ToString("N");
    }

    public bool IsLockedOut(DateTimeOffset now) => LockoutEnabled && LockoutEnd > now;

    public void RegisterFailedLogin(DateTimeOffset now)
    {
        AccessFailedCount++;
        if (AccessFailedCount >= MaxFailedAttempts)
        {
            LockoutEnd = now + LockoutDuration;
            AccessFailedCount = 0;
        }
    }

    public void RegisterSuccessfulLogin()
    {
        AccessFailedCount = 0;
        LockoutEnd = null;
    }
}

internal sealed class Tenant : AggregateRoot
{
    private Tenant()
    {
    }

    public string Name { get; private set; } = null!;
    public string DocumentType { get; private set; } = null!;
    public string DocumentNumber { get; private set; } = null!;
    public string City { get; private set; } = null!;
    public string State { get; private set; } = null!;
    public string TimeZone { get; private set; } = null!;
    public string TermsVersion { get; private set; } = null!;
    public DateTimeOffset TermsAcceptedAt { get; private set; }
    public Guid TermsAcceptedByUserId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static Tenant Create(
        Guid id, string name, string documentType, string documentNumber, string city, string state, string timeZone,
        string termsVersion, Guid acceptedBy, DateTimeOffset now) => new()
    {
        Id = id,
        Name = name.Trim(),
        DocumentType = documentType,
        DocumentNumber = documentNumber,
        City = city.Trim(),
        State = state.ToUpperInvariant(),
        TimeZone = timeZone,
        TermsVersion = termsVersion,
        TermsAcceptedAt = now,
        TermsAcceptedByUserId = acceptedBy,
        CreatedAt = now,
    };
}

/// tenant_settings: a chave é o próprio tenant (Id = TenantId).
internal sealed class TenantSettings : AggregateRoot, ITenantOwned, ISyncTracked
{
    public static readonly string[] AllPaymentMethods = ["CASH", "PIX", "CARD"];

    private TenantSettings()
    {
    }

    public Guid TenantId => Id;
    public string SyncEntityType => "TENANT_SETTINGS";
    public Money? KiloPricePerKg { get; private set; }
    public Money DefaultDeliveryFee { get; private set; } = Money.Zero;
    public List<string> EnabledPaymentMethods { get; private set; } = [.. AllPaymentMethods];
    public int SharedDeviceIdleSeconds { get; private set; } = 120;

    public static TenantSettings CreateDefault(Guid tenantId) => new() { Id = tenantId };
}

internal sealed class TenantEmployee : AggregateRoot, ITenantOwned, ISyncTracked
{
    private readonly List<TenantEmployeeRole> _roles = [];

    private TenantEmployee()
    {
    }

    public Guid TenantId { get; private set; }
    public string SyncEntityType => "EMPLOYEE";
    public Guid UserId { get; private set; }
    public string DisplayName { get; private set; } = null!;
    public string Status { get; private set; } = EmployeeStatus.Active;
    public string? PinHash { get; private set; }
    public int PinFailedAttempts { get; private set; }
    public DateTimeOffset? PinLockedUntil { get; private set; }
    public Guid SecurityStamp { get; private set; }
    public DateTimeOffset JoinedAt { get; private set; }
    public DateTimeOffset? DeactivatedAt { get; private set; }
    public Guid? DeactivatedByEmployeeId { get; private set; }
    public IReadOnlyList<TenantEmployeeRole> Roles => _roles;

    public bool IsActive => Status == EmployeeStatus.Active;

    public IReadOnlyList<Role> RoleValues => _roles.Select(r => RolePermissions.FromApiName(r.Role)).ToList();

    public static TenantEmployee Create(Guid id, Guid tenantId, Guid userId, string displayName, IEnumerable<Role> roles, DateTimeOffset now)
    {
        var employee = new TenantEmployee
        {
            Id = id,
            TenantId = tenantId,
            UserId = userId,
            DisplayName = displayName.Trim(),
            SecurityStamp = Guid.NewGuid(),
            JoinedAt = now,
        };
        foreach (var role in roles.Distinct())
        {
            employee._roles.Add(new TenantEmployeeRole(tenantId, id, role.ToApiName()));
        }

        return employee;
    }
}

internal static class EmployeeStatus
{
    public const string Active = "ACTIVE";
    public const string Inactive = "INACTIVE";
}

internal sealed class TenantEmployeeRole(Guid tenantId, Guid tenantEmployeeId, string role) : ITenantOwned
{
    public Guid TenantId { get; private set; } = tenantId;
    public Guid TenantEmployeeId { get; private set; } = tenantEmployeeId;
    public string Role { get; private set; } = role;
}

/// Refresh token opaco e rotativo; só o hash é gravado (ADR-0027). Tabela de identidade, sem RLS.
internal sealed class RefreshToken : Entity
{
    private RefreshToken()
    {
    }

    public Guid UserId { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid TenantEmployeeId { get; private set; }
    public string InstallationId { get; private set; } = null!;
    public Guid? SharedDeviceId { get; private set; }
    public string TokenHash { get; private set; } = null!;
    public Guid FamilyId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public Guid? ReplacedByTokenId { get; private set; }

    public static RefreshToken Create(
        Guid id, Guid userId, Guid tenantId, Guid employeeId, string installationId, Guid? sharedDeviceId,
        string tokenHash, Guid familyId, DateTimeOffset now, TimeSpan lifetime) => new()
    {
        Id = id,
        UserId = userId,
        TenantId = tenantId,
        TenantEmployeeId = employeeId,
        InstallationId = installationId,
        SharedDeviceId = sharedDeviceId,
        TokenHash = tokenHash,
        FamilyId = familyId,
        CreatedAt = now,
        ExpiresAt = now + lifetime,
    };

    public bool IsUsable(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;

    public void Revoke(DateTimeOffset now, Guid? replacedBy = null)
    {
        RevokedAt ??= now;
        ReplacedByTokenId ??= replacedBy;
    }
}
