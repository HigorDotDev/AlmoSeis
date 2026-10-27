using AlmoSeis.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AlmoSeis.Infrastructure.Tenancy;

/// Reaplica app.tenant_id/app.user_id na conexão já aberta. Necessário quando o contexto de tenant é
/// definido no meio da transação (autocadastro, login, aceite de convite), depois que o decorator
/// transacional abriu a conexão (05 §5.2).
public interface ITenantSession
{
    Task ApplyAsync(CancellationToken cancellationToken);

    /// Só no aceite de convite (05 §5.12, grupo 3).
    Task SetInvitationCodeAsync(string? code, CancellationToken cancellationToken);
}

internal sealed class TenantSession(AppDbContext db, ITenantContext tenant) : ITenantSession
{
    public async Task ApplyAsync(CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId?.ToString() ?? string.Empty;
        var userId = tenant.UserId?.ToString() ?? string.Empty;
        await db.Database.ExecuteSqlAsync(
            $"SELECT set_config('app.tenant_id', {tenantId}, false), set_config('app.user_id', {userId}, false)",
            cancellationToken);
    }

    public async Task SetInvitationCodeAsync(string? code, CancellationToken cancellationToken) =>
        await db.Database.ExecuteSqlAsync(
            $"SELECT set_config('app.invitation_code', {code ?? string.Empty}, false)", cancellationToken);
}
