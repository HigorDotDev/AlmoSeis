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
public sealed record StoredResponse(int Status, string? Body);

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
        var userId = tenant.UserId ?? throw new InvalidOperationException("Idempotência exige usuário.");
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

        // Outro usuário, outra rota ou outro corpo: a chave não pode ser reaproveitada.
        if (existing.UserId != userId || existing.Endpoint != endpoint || existing.RequestHash != requestHash)
        {
            throw new BusinessException(ErrorCodes.IdempotencyKeyReused);
        }

        // Só registros COMPLETED ficam visíveis: o IN_PROGRESS de outra transação é desfeito ou concluído no commit dela.
        return new IdempotencyBegin.Replay(new StoredResponse(existing.ResponseStatus!.Value, existing.ResponseBody));
    }

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
