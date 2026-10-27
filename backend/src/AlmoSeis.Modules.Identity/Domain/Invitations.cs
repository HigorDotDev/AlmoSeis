using System.Security.Cryptography;
using AlmoSeis.SharedKernel.Domain;
using AlmoSeis.SharedKernel.Errors;
using AlmoSeis.SharedKernel.Security;

namespace AlmoSeis.Modules.Identity.Domain;

/// Vira `CatalogUpdated` com `entityType: TENANT_SETTINGS` (06 §6.14).
internal sealed record TenantSettingsChanged(Guid TenantId, DateTimeOffset OccurredAt) : IDomainEvent;

/// PIN do aparelho compartilhado (ADR-0027): 4 a 6 dígitos, sem sequências nem repetições.
internal static class PinPolicy
{
    public const int MaxAttempts = 5;
    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15);

    public static string? Problem(string pin)
    {
        if (pin.Length is < 4 or > 6 || !pin.All(char.IsAsciiDigit))
        {
            return "O PIN deve ter de 4 a 6 dígitos.";
        }

        var digits = pin.Select(c => c - '0').ToArray();
        var steps = digits.Zip(digits.Skip(1), (a, b) => b - a).ToArray();
        var trivial = steps.All(d => d == 0) || steps.All(d => d == 1) || steps.All(d => d == -1);
        return trivial ? "Escolha um PIN menos óbvio (sem sequências ou números repetidos)." : null;
    }
}

/// Convite de funcionário ou código de redefinição de senha (ADR-0018). Criado por SQL (código único entre
/// pendentes de todos os tenants), lido e alterado pelo EF.
internal sealed class EmployeeInvitation : Entity, ITenantOwned
{
    public static readonly TimeSpan InviteLifetime = TimeSpan.FromDays(3);
    public static readonly TimeSpan PasswordResetLifetime = TimeSpan.FromHours(24);

    private EmployeeInvitation()
    {
    }

    public Guid TenantId { get; private set; }
    public string Purpose { get; private set; } = null!;
    public string Code { get; private set; } = null!;
    public List<string>? Roles { get; private set; }
    public Guid? TargetEmployeeId { get; private set; }
    public string Status { get; private set; } = null!;
    public DateTimeOffset ExpiresAt { get; private set; }
    public Guid CreatedByEmployeeId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public Guid? AcceptedByUserId { get; private set; }
    public DateTimeOffset? AcceptedAt { get; private set; }

    public IReadOnlyList<Role> RoleValues => (Roles ?? []).Select(RolePermissions.FromApiName).ToList();

    /// Pendente e dentro da validade: só assim o código vale (vencido e ainda pendente conta como expirado).
    public bool IsUsable(DateTimeOffset now) => Status == InvitationStatus.Pending && ExpiresAt > now;

    public string EffectiveStatus(DateTimeOffset now) =>
        Status == InvitationStatus.Pending && ExpiresAt <= now ? InvitationStatus.Expired : Status;

    public void Accept(Guid userId, DateTimeOffset now)
    {
        if (!IsUsable(now))
        {
            throw new BusinessException(ErrorCodes.InvitationNotPending);
        }

        (Status, AcceptedByUserId, AcceptedAt) = (InvitationStatus.Accepted, userId, now);
    }

    public void Revoke(DateTimeOffset now)
    {
        if (!IsUsable(now))
        {
            throw new BusinessException(ErrorCodes.InvitationNotPending);
        }

        Status = InvitationStatus.Revoked;
    }
}

internal static class InvitationStatus
{
    public const string Pending = "PENDING";
    public const string Accepted = "ACCEPTED";
    public const string Revoked = "REVOKED";
    public const string Expired = "EXPIRED";
}

internal static class InvitationPurpose
{
    public const string Invite = "INVITE";
    public const string PasswordReset = "PASSWORD_RESET";
}

/// 6 caracteres de `[A-HJ-NP-Z2-9]` (sem I, O, 0 e 1, que se confundem ao ditar) (07 §7.6).
internal static class InvitationCodeGenerator
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string New() =>
        string.Create(6, 0, (span, _) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                span[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
            }
        });
}
