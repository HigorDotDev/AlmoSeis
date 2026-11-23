using AlmoSeis.Infrastructure.Audit;
using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Tenancy;
using AlmoSeis.Modules.Identity.Contracts;
using AlmoSeis.Modules.Identity.Domain;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Errors;
using AlmoSeis.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;

namespace AlmoSeis.Modules.Identity.Application;

/// POST /admin/tenants/{id}/behaviors (06 §6.15, ADR-0054 item 5). O administrador não tem tenant no token: o handler
/// assume a sessão do tenant da rota (`InitializeForPlatformAdmin`) e grava com o papel da aplicação, para o acréscimo ter
/// RLS, feed (`TENANT`) e outbox (`TenantModulesChanged`, Q-324) na mesma transação, como qualquer escrita do tenant.
/// Auditoria `PLATFORM_ADMIN` com o administrador como ator. A sessão leva `app.user_id` vazio (`SessionUserId`): as
/// políticas por usuário de 05 §5.12 (grupo 2) não abrem os vínculos do administrador em outros tenants (ADR-0025,
/// complemento de 2026-11-23). A resposta leva só os comportamentos, rótulos e módulos.
internal sealed class AddTenantBehaviorHandler(
    AppDbContext db,
    ITenantContext tenant,
    ITenantSession session,
    IAuditWriter audit,
    TimeProvider time) : ICommandHandler<AddTenantBehaviorCommand, TenantBehaviorsDto>
{
    public async Task<TenantBehaviorsDto> HandleAsync(AddTenantBehaviorCommand command, CancellationToken cancellationToken)
    {
        var request = command.Request;
        var reason = request.Reason?.Trim() ?? string.Empty;
        if (request.Behavior is not { } behavior)
        {
            throw BusinessException.Validation("behavior", "Informe o comportamento: RESTAURANT ou SNACK_BAR.");
        }

        if (reason.Length < 5)
        {
            throw BusinessException.Validation("reason", "Descreva o motivo com pelo menos 5 caracteres.");
        }

        tenant.InitializeForPlatformAdmin(command.TenantId, command.AdminUserId);
        await session.ApplyAsync(cancellationToken);

        // RLS: só o tenant da sessão é visível. Inexistente → 404, como as outras rotas de /admin.
        var target = await db.Set<Tenant>().Include(t => t.Behaviors).Include(t => t.Labels)
            .SingleOrDefaultAsync(t => t.Id == command.TenantId, cancellationToken)
            ?? throw new NotFoundException("TENANT", command.TenantId);

        var before = target.EffectiveBehaviors.Select(ModuleNames.ToApiName).ToList();
        if (target.AddBehavior(behavior, command.AdminUserId, time.GetUtcNow()))
        {
            audit.Add(
                "TenantBehaviorAdded", target.SyncEntityType, target.Id,
                before: new { behaviors = before },
                after: new
                {
                    behaviors = target.EffectiveBehaviors.Select(ModuleNames.ToApiName).ToList(),
                    added = ModuleNames.ToApiName(behavior),
                    modules = target.Modules.Select(ModuleNames.ToApiName).ToList(),
                    reason,
                },
                origin: AuditOrigins.PlatformAdmin);
        }

        return new TenantBehaviorsDto(target.Id, target.EffectiveBehaviors, target.EffectiveLabels, TenantModuleMap.For(target.EffectiveBehaviors));
    }
}
