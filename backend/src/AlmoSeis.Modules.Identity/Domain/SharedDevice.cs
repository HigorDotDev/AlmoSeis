using AlmoSeis.SharedKernel.Domain;

namespace AlmoSeis.Modules.Identity.Domain;

/// Aparelho compartilhado (ADR-0027): tablet da cozinha ou do caixa onde cada funcionário entra pelo PIN.
/// Guarda só o hash do `deviceSecret`.
internal sealed class SharedDevice : Entity, ITenantOwned
{
    private SharedDevice()
    {
    }

    public Guid TenantId { get; private set; }
    public string InstallationId { get; private set; } = null!;
    public string Name { get; private set; } = null!;
    public string Platform { get; private set; } = null!;
    public string SecretHash { get; private set; } = null!;
    public Guid RegisteredByEmployeeId { get; private set; }
    public DateTimeOffset RegisteredAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }

    public static SharedDevice Register(
        Guid id, Guid tenantId, string installationId, string name, string platform, string secretHash, Guid by, DateTimeOffset now) => new()
    {
        Id = id,
        TenantId = tenantId,
        InstallationId = installationId,
        Name = name.Trim(),
        Platform = platform,
        SecretHash = secretHash,
        RegisteredByEmployeeId = by,
        RegisteredAt = now,
    };

    public bool Revoke(DateTimeOffset now)
    {
        if (RevokedAt is not null)
        {
            return false;
        }

        RevokedAt = now;
        return true;
    }
}
