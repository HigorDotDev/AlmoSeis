using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Persistence.Entities;
using AlmoSeis.Infrastructure.Tenancy;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Errors;
using AlmoSeis.SharedKernel.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using AlmoSeis.Modules.Sync.Contracts;
using Npgsql;

namespace AlmoSeis.Modules.Sync;

public static class SyncModule
{
    public static IServiceCollection AddSyncModule(this IServiceCollection services)
    {
        services.AddScoped<IQueryHandler<GetSyncChangesQuery, SyncChangesDto>, GetSyncChangesQueryHandler>();
        services.AddScoped<IQueryHandler<GetOperationQuery, OperationDto>, GetOperationQueryHandler>();
        return services;
    }
}

/// GET /sync/changes (ADR-0032): compactado (a última alteração de cada entidade), filtrado pelo que quem chama pode ler.
internal sealed class GetSyncChangesQueryHandler(AppDbContext db, ITenantContext tenant, IPermissionChecker permissions)
    : IQueryHandler<GetSyncChangesQuery, SyncChangesDto>
{
    public const int MaxLimit = 500;

    /// Mapa de leitura por `entityType` (06 §6.12): o tipo some do feed para quem não pode fazer o GET correspondente.
    private static readonly IReadOnlyDictionary<string, string> RequiredPermission = new Dictionary<string, string>
    {
        ["TENANT_SETTINGS"] = Permissions.Orders.View,
        ["PRODUCT"] = Permissions.Orders.View,
        ["MARMITA_SIZE"] = Permissions.Orders.View,
        ["MENU"] = Permissions.Orders.View,
        ["CUSTOMER"] = Permissions.Orders.View,
        ["ORDER"] = Permissions.Orders.View,
        ["PAYMENT"] = Permissions.Orders.View,
        ["PAYMENT_REVERSAL"] = Permissions.Orders.View,
        ["BUSINESS_DAY"] = Permissions.Orders.View,
        ["CASH_MOVEMENT"] = Permissions.Closing.View,
        ["DAILY_CLOSING"] = Permissions.Closing.View,
        ["CLOSING_ADJUSTMENT"] = Permissions.Closing.View,
        ["EMPLOYEE"] = Permissions.EmployeesManage,
    };

    public async Task<SyncChangesDto> HandleAsync(GetSyncChangesQuery query, CancellationToken cancellationToken)
    {
        if (query.Limit is < 1 or > MaxLimit)
        {
            throw BusinessException.Validation("limit", $"Use um limite entre 1 e {MaxLimit}.");
        }

        if (query.Since < 0)
        {
            throw BusinessException.Validation("since", "Use 0 para começar do zero.");
        }

        var tenantId = tenant.TenantId!.Value;
        var currentSeq = (await db.Database
            .SqlQuery<long>($"SELECT last_seq AS \"Value\" FROM tenant_sync_counters WHERE tenant_id = {tenantId}")
            .ToListAsync(cancellationToken)).SingleOrDefault();

        // Os seq de um tenant são contíguos: se o primeiro retido está depois de since+1, houve limpeza no meio.
        if (query.Since > 0 && query.Since < currentSeq)
        {
            var firstRetained = await db.Set<ChangeFeedEntry>().Where(e => e.TenantId == tenantId).MinAsync(e => (long?)e.Seq, cancellationToken);
            if (firstRetained is null || firstRetained > query.Since + 1)
            {
                throw new BusinessException(
                    ErrorCodes.SyncCursorExpired,
                    extensions: new Dictionary<string, object?> { ["currentSeq"] = currentSeq });
            }
        }

        var allowed = RequiredPermission.Where(p => permissions.Has(p.Value)).Select(p => p.Key).ToArray();
        var self = tenant.EmployeeId;

        // DISTINCT ON de 05 §5.14, ordenado por seq para paginar; limit+1 diz se há mais.
        var rows = await db.Database.SqlQuery<FeedRow>(
            $"""
            SELECT seq, entity_type, entity_id, entity_version, change_kind FROM (
              SELECT DISTINCT ON (entity_type, entity_id) seq, entity_type, entity_id, entity_version, change_kind
                FROM change_feed_entries
               WHERE tenant_id = {tenantId} AND seq > {query.Since}
                 AND (entity_type = ANY({allowed}) OR (entity_type = 'EMPLOYEE' AND entity_id = {self}))
               ORDER BY entity_type, entity_id, seq DESC
            ) latest
            ORDER BY seq
            LIMIT {query.Limit + 1}
            """).ToListAsync(cancellationToken);

        var hasMore = rows.Count > query.Limit;
        var page = rows.Take(query.Limit).ToList();

        // Sem mais páginas, o cursor vai até o contador: tipos filtrados não voltam a ser varridos.
        var lastSeq = hasMore ? page[^1].Seq : Math.Max(currentSeq, query.Since);
        return new SyncChangesDto(
            page.Select(r => new SyncChangeDto(r.Seq, r.EntityType, r.EntityId, r.EntityVersion, r.ChangeKind)).ToList(), lastSeq, hasMore);
    }

    private sealed record FeedRow(long Seq, string EntityType, Guid EntityId, int EntityVersion, string ChangeKind);
}

/// GET /operations/{key} (ADR-0033 §5): resolve operação de resultado desconhecido. Só as chaves de quem chama.
internal sealed class GetOperationQueryHandler(AppDbContext db, ITenantContext tenant, TimeProvider time)
    : IQueryHandler<GetOperationQuery, OperationDto>
{
    private const string LockNotAvailable = "55P03";

    public async Task<OperationDto> HandleAsync(GetOperationQuery query, CancellationToken cancellationToken)
    {
        var record = await db.Set<IdempotencyRecord>().AsNoTracking()
            .SingleOrDefaultAsync(r => r.Key == query.Key, cancellationToken);
        if (record is not null)
        {
            return record.UserId == tenant.UserId && record.ExpiresAt > time.GetUtcNow()
                // A operação conclui na mesma transação em que o registro nasce: completedAt = createdAt.
                ? new OperationDto(record.Key, "COMPLETED", record.Endpoint, record.ResponseStatus, record.ResourceId, record.CreatedAt, record.CreatedAt)
                : NotFound(query.Key);
        }

        return await IsInProgressAsync(query.Key, cancellationToken)
            ? new OperationDto(query.Key, "IN_PROGRESS", null, null, null, null, null)
            : NotFound(query.Key);
    }

    /// O registro IN_PROGRESS só existe dentro da transação que executa a operação, invisível a um SELECT.
    /// Uma inserção de sondagem com a mesma chave esbarra na trava dela; a sondagem é sempre desfeita.
    private async Task<bool> IsInProgressAsync(Guid key, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var now = time.GetUtcNow();
            await db.Database.ExecuteSqlAsync($"SELECT set_config('lock_timeout', '100ms', true)", cancellationToken);
            await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO idempotency_records (id, tenant_id, key, user_id, endpoint, request_hash, status, created_at, expires_at)
                VALUES ({Guid.NewGuid()}, {tenant.TenantId!.Value}, {key}, {tenant.UserId!.Value}, 'probe', {new string('0', 64)}, 'IN_PROGRESS', {now}, {now.AddMinutes(1)})
                ON CONFLICT (tenant_id, key) DO NOTHING
                """,
                cancellationToken);
            return false;
        }
        catch (PostgresException ex) when (ex.SqlState == LockNotAvailable)
        {
            return true;
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None);
        }
    }

    private static OperationDto NotFound(Guid key) => new(key, "NOT_FOUND", null, null, null, null, null);
}
