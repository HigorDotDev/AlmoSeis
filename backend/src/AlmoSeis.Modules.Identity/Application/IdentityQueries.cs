using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Tenancy;
using AlmoSeis.Modules.Identity.Contracts;
using AlmoSeis.Modules.Identity.Domain;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Errors;
using AlmoSeis.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;

namespace AlmoSeis.Modules.Identity.Application;

/// GET /auth/me.
internal sealed class GetMeQueryHandler(AppDbContext db, ITenantContext tenant, AuthSessionIssuer sessions) : IQueryHandler<GetMeQuery, MeDto>
{
    public async Task<MeDto> HandleAsync(GetMeQuery query, CancellationToken cancellationToken)
    {
        var employeeId = tenant.EmployeeId ?? throw new BusinessException(ErrorCodes.Unauthenticated);
        var employee = await db.Set<TenantEmployee>().AsNoTracking().Include(e => e.Roles)
            .SingleOrDefaultAsync(e => e.Id == employeeId, cancellationToken)
            ?? throw new BusinessException(ErrorCodes.Unauthenticated);
        var user = await db.Set<User>().AsNoTracking().SingleAsync(u => u.Id == employee.UserId, cancellationToken);
        return await sessions.BuildMeAsync(user, employee, tenant.SharedDeviceId, cancellationToken);
    }
}

/// GET /roles: o que cada perfil pode fazer (06 §6.2).
internal sealed class ListRolesQueryHandler : IQueryHandler<ListRolesQuery, IReadOnlyList<RoleDto>>
{
    private static readonly IReadOnlyDictionary<Role, string> Names = new Dictionary<Role, string>
    {
        [Role.Owner] = "Proprietário",
        [Role.Manager] = "Gerente",
        [Role.Attendant] = "Atendente",
        [Role.Kitchen] = "Cozinha",
        [Role.Cashier] = "Caixa",
    };

    public Task<IReadOnlyList<RoleDto>> HandleAsync(ListRolesQuery query, CancellationToken cancellationToken)
    {
        IReadOnlyList<RoleDto> roles = Enum.GetValues<Role>()
            .Select(role =>
            {
                var permissions = RolePermissions.For(role);
                return new RoleDto(role.ToApiName(), Names[role], Permissions.All.Where(permissions.Contains).ToList());
            })
            .ToList();
        return Task.FromResult(roles);
    }
}

/// Port da validação do token na Api (07 §7.4).
internal sealed class EmployeeAccessReader(AppDbContext db) : IEmployeeAccessReader
{
    public async Task<EmployeeAccess?> GetAsync(Guid employeeId, CancellationToken cancellationToken)
    {
        var employee = await db.Set<TenantEmployee>().AsNoTracking().Include(e => e.Roles)
            .SingleOrDefaultAsync(e => e.Id == employeeId, cancellationToken);
        if (employee is null)
        {
            return null;
        }

        var roles = employee.Roles.Select(r => r.Role).Order().ToList();
        return new EmployeeAccess(employee.Id, employee.TenantId, employee.IsActive, employee.SecurityStamp, roles, RolePermissions.For(employee.RoleValues));
    }
}

internal sealed class EmployeeDirectory(AppDbContext db) : IEmployeeDirectory
{
    public async Task<IReadOnlyDictionary<Guid, string>> GetDisplayNamesAsync(IEnumerable<Guid> employeeIds, CancellationToken cancellationToken)
    {
        var ids = employeeIds.Distinct().ToList();
        return await db.Set<TenantEmployee>().AsNoTracking()
            .Where(e => ids.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.DisplayName, cancellationToken);
    }
}

internal sealed class TenantCalendar(AppDbContext db, ITenantContext tenant, TimeProvider time) : ITenantCalendar
{
    public async Task<DateOnly> TodayAsync(CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Data local sem tenant.");
        var timeZone = await db.Set<Tenant>().AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => t.TimeZone)
            .SingleAsync(cancellationToken);

        var local = TimeZoneInfo.ConvertTime(time.GetUtcNow(), TimeZoneInfo.FindSystemTimeZoneById(timeZone));
        return DateOnly.FromDateTime(local.DateTime);
    }
}

internal sealed class TenantSettingsReader(AppDbContext db, ITenantContext tenant) : ITenantSettingsReader
{
    public async Task<TenantOperationalSettings> GetAsync(CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Configuração sem tenant.");
        var settings = await db.Set<TenantSettings>().AsNoTracking().SingleAsync(s => s.Id == tenantId, cancellationToken);
        return new TenantOperationalSettings(settings.KiloPricePerKg, settings.DefaultDeliveryFee, settings.EnabledPaymentMethods);
    }
}
