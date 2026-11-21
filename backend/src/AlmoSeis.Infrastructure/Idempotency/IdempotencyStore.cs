using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Persistence.Entities;
using AlmoSeis.Infrastructure.Tenancy;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AlmoSeis.Infrastructure.Idempotency;

public sealed class IdempotencyOptions
{
    public const string Section = "Idempotency";

    public int TtlHours { get; set; } = 72;

    public int LockTimeoutSeconds { get; set; } = 5;
}

/// Resposta guardada de uma operação concluída (ADR-0033).
public sealed record StoredResponse(int Status, string? Body, Guid? ResourceId = null);

/// Campo da resposta que leva um segredo do cliente e por isso **não** fica no corpo guardado (ADR-0047: o token de
/// acompanhamento só existe em hash). O filtro tira o campo antes de guardar e, no reenvio, pede o valor de novo a quem
/// sabe recalculá-lo a partir do recurso criado (`resource_id`), no tenant da sessão.
public interface IReplaySecretSource
{
    /// Nome do campo no JSON (camelCase), ex.: `trackingToken`.
    string Field { get; }

    string Recreate(Guid tenantId, Guid resourceId);
}

public abstract record IdempotencyBegin
{
    /// Registro IN_PROGRESS inserido na transação corrente: executar a operação.
    public sealed record Started : IdempotencyBegin;

    /// Mesma chave, mesmo corpo, já concluída: devolver a resposta original.
    public sealed record Replay(StoredResponse Response) : IdempotencyBegin;
}

/// 07 §7.3. Opera na transação corrente do AppDbContext, aberta pelo IdempotencyFilter:
/// o registro e a operação são gravados (ou desfeitos) juntos.
public interface IIdempotencyStore
{
    /// `KeyReused` (422) e `OperationInProgress` (409) saem como BusinessException.
    Task<IdempotencyBegin> TryBeginAsync(Guid key, string endpoint, string requestHash, CancellationToken cancellationToken);

    /// Sem transação nem gravação: a resposta de uma operação já concluída com a mesma chave, rota e corpo (nulo se não
    /// há). Usado antes da transação nas rotas públicas, para o reenvio não repetir o Turnstile nem o mapa (07 §7.4).
    Task<StoredResponse?> FindCompletedAsync(Guid key, string endpoint, string requestHash, CancellationToken cancellationToken);

    Task CompleteAsync(Guid key, int responseStatus, string? responseBody, Guid? resourceId, CancellationToken cancellationToken);
}

internal sealed class IdempotencyStore(
    AppDbContext db,
    ITenantContext tenant,
    IIdGenerator ids,
    TimeProvider time,
    IOptions<IdempotencyOptions> options) : IIdempotencyStore
{
    private const string LockNotAvailable = "55P03";

    public async Task<IdempotencyBegin> TryBeginAsync(Guid key, string endpoint, string requestHash, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Idempotência exige tenant.");
        // Página de pedidos: anônima, com escopo (tenant, key) e user_id nulo (ADR-0045 item 10).
        Guid? userId = tenant.IsAnonymous ? null : tenant.UserId ?? throw new InvalidOperationException("Idempotência exige usuário.");
        var now = time.GetUtcNow();
        var lockTimeout = $"{options.Value.LockTimeoutSeconds}s";

        // ON CONFLICT DO NOTHING espera a transação concorrente com a mesma chave terminar, sem abortar a nossa
        // quando ela confirma. O lock_timeout vale só para este INSERT.
        int inserted;
        try
        {
            await db.Database.ExecuteSqlAsync($"SELECT set_config('lock_timeout', {lockTimeout}, true)", cancellationToken);
            inserted = await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO idempotency_records (id, tenant_id, key, user_id, endpoint, request_hash, status, created_at, expires_at)
                VALUES ({ids.New()}, {tenantId}, {key}, {userId}, {endpoint}, {requestHash}, 'IN_PROGRESS', {now}, {now.AddHours(options.Value.TtlHours)})
                ON CONFLICT (tenant_id, key) DO NOTHING
                """,
                cancellationToken);
            await db.Database.ExecuteSqlAsync($"SELECT set_config('lock_timeout', '0', true)", cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == LockNotAvailable)
        {
            throw new BusinessException(ErrorCodes.OperationInProgress);
        }

        if (inserted == 1)
        {
            return new IdempotencyBegin.Started();
        }

        var existing = await db.Set<IdempotencyRecord>().AsNoTracking()
            .SingleAsync(r => r.Key == key, cancellationToken);

        // Só registros COMPLETED ficam visíveis: o IN_PROGRESS de outra transação é desfeito ou concluído no commit dela.
        return new IdempotencyBegin.Replay(Replayable(existing, userId, endpoint, requestHash));
    }

    public async Task<StoredResponse?> FindCompletedAsync(Guid key, string endpoint, string requestHash, CancellationToken cancellationToken)
    {
        Guid? userId = tenant.IsAnonymous ? null : tenant.UserId;
        var existing = await db.Set<IdempotencyRecord>().AsNoTracking()
            .SingleOrDefaultAsync(r => r.Key == key && r.Status == "COMPLETED", cancellationToken);
        return existing is null ? null : Replayable(existing, userId, endpoint, requestHash);
    }

    /// Outro usuário (ou anônimo × autenticado), outra rota ou outro corpo: a chave não pode ser reaproveitada.
    private static StoredResponse Replayable(IdempotencyRecord existing, Guid? userId, string endpoint, string requestHash) =>
        existing.UserId != userId || existing.Endpoint != endpoint || existing.RequestHash != requestHash
            ? throw new BusinessException(ErrorCodes.IdempotencyKeyReused)
            : new StoredResponse(existing.ResponseStatus!.Value, existing.ResponseBody, existing.ResourceId);

    public async Task CompleteAsync(Guid key, int responseStatus, string? responseBody, Guid? resourceId, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId!.Value;
        var status = (short)responseStatus;
        await db.Database.ExecuteSqlAsync(
            $"""
            UPDATE idempotency_records
               SET status = 'COMPLETED', response_status = {status}, response_body = {responseBody}::jsonb, resource_id = {resourceId}::uuid
             WHERE tenant_id = {tenantId} AND key = {key}
            """,
            cancellationToken);
    }
}
