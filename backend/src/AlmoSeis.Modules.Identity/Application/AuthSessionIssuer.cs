using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Security;
using AlmoSeis.Infrastructure.Tenancy;
using AlmoSeis.Modules.Billing.Contracts;
using AlmoSeis.Modules.Identity.Contracts;
using AlmoSeis.Modules.Identity.Domain;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlmoSeis.Modules.Identity.Application;

/// Emite access token + refresh token rotativo e monta o `Me` de 06 §6.3.
internal sealed class AuthSessionIssuer(
    AppDbContext db,
    IJwtTokenIssuer jwt,
    ISecretHasher hasher,
    IIdGenerator ids,
    IBillingPort billing,
    ITenantContext tenant,
    IOptions<JwtOptions> jwtOptions,
    TimeProvider time)
{
    public async Task<AuthResultDto> IssueAsync(
        User user, TenantEmployee employee, string installationId, Guid? sharedDeviceId, Guid? refreshFamilyId, CancellationToken cancellationToken)
    {
        var tokens = IssueTokens(user, employee, installationId, sharedDeviceId, refreshFamilyId, out _);
        var me = await BuildMeAsync(user, employee, sharedDeviceId, cancellationToken);
        return new AuthResultDto(tokens, me);
    }

    public TokensDto IssueTokens(
        User user, TenantEmployee employee, string installationId, Guid? sharedDeviceId, Guid? refreshFamilyId, out RefreshToken refreshToken)
    {
        var now = time.GetUtcNow();
        var access = jwt.Issue(new AccessTokenRequest(user.Id, employee.TenantId, employee.Id, employee.SecurityStamp, sharedDeviceId, installationId, user.IsPlatformAdmin));

        var rawRefresh = SecureRandom.Token("rt_");
        refreshToken = RefreshToken.Create(
            ids.New(), user.Id, employee.TenantId, employee.Id, installationId, sharedDeviceId,
            hasher.Hash(rawRefresh), refreshFamilyId ?? ids.New(), now, TimeSpan.FromDays(jwtOptions.Value.RefreshTokenDays));
        db.Add(refreshToken);

        return new TokensDto("Bearer", access.Token, access.ExpiresAt, rawRefresh, refreshToken.ExpiresAt);
    }

    public async Task<MeDto> BuildMeAsync(User user, TenantEmployee employee, Guid? sharedDeviceId, CancellationToken cancellationToken)
    {
        var tenantRow = await db.Set<Tenant>().AsNoTracking().SingleAsync(t => t.Id == employee.TenantId, cancellationToken);
        var roles = employee.Roles.Select(r => r.Role).Order().ToList();
        var permissions = RolePermissions.For(employee.RoleValues);

        var subscription = await billing.GetSummaryAsync(employee.TenantId, cancellationToken);
        var subscriptionDto = permissions.Contains(Permissions.SubscriptionView) && subscription is not null
            ? new MeSubscriptionDto(subscription.Status, subscription.TrialEndsAt, subscription.DaysLeft, subscription.CanWrite)
            : new MeSubscriptionDto(null, null, null, subscription?.CanWrite ?? true);

        // No autocadastro/aceite o vínculo ainda não foi persistido (o UnitOfWork grava no commit): inclui o corrente.
        var memberships = (await ListMembershipsAsync(user.Id, cancellationToken)).ToList();
        if (memberships.All(m => m.EmployeeId != employee.Id))
        {
            memberships.Insert(0, new MembershipDto(employee.TenantId, tenantRow.Name, employee.Id, roles));
        }

        return new MeDto(
            new MeUserDto(user.Id, user.FullName, user.Email, user.PhoneNumber),
            new MeTenantDto(tenantRow.Id, tenantRow.Name, tenantRow.TimeZone),
            new MeEmployeeDto(employee.Id, employee.DisplayName, roles, employee.PinHash is not null),
            Permissions.All.Where(permissions.Contains).ToList(),
            memberships,
            subscriptionDto,
            sharedDeviceId is { } deviceId
                ? new MeSharedDeviceDto(deviceId, await db.Set<SharedDevice>().AsNoTracking().Where(d => d.Id == deviceId).Select(d => d.Name).SingleOrDefaultAsync(cancellationToken) ?? string.Empty)
                : null,
            time.GetUtcNow());
    }

    /// Vínculos ativos do próprio usuário em qualquer tenant. Exceção documentada ao filtro de tenant do EF:
    /// a RLS libera as linhas por app.user_id (05 §5.12, grupo 2) e a consulta filtra o usuário explicitamente.
    public async Task<IReadOnlyList<MembershipDto>> ListMembershipsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var employees = await db.Set<TenantEmployee>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(e => e.Roles)
            .Where(e => e.UserId == userId && e.Status == EmployeeStatus.Active)
            .OrderBy(e => e.JoinedAt)
            .ToListAsync(cancellationToken);

        var tenantIds = employees.Select(e => e.TenantId).ToList();
        var names = await db.Set<Tenant>().AsNoTracking()
            .Where(t => tenantIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);

        return employees
            .Select(e => new MembershipDto(e.TenantId, names.GetValueOrDefault(e.TenantId, string.Empty), e.Id, e.Roles.Select(r => r.Role).Order().ToList()))
            .ToList();
    }

    /// Carrega o funcionário ativo do usuário no tenant (já com o tenant aplicado na sessão).
    public Task<TenantEmployee?> FindActiveEmployeeAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken) =>
        db.Set<TenantEmployee>()
            .IgnoreQueryFilters()
            .Include(e => e.Roles)
            .SingleOrDefaultAsync(e => e.UserId == userId && e.TenantId == tenantId && e.Status == EmployeeStatus.Active, cancellationToken);

    public Guid? CurrentTenantId => tenant.TenantId;
}
