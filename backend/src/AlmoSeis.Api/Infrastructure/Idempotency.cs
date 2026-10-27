using System.Text.Json;
using AlmoSeis.Infrastructure.Idempotency;
using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Security;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Errors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Options;

namespace AlmoSeis.Api.Infrastructure;

/// Endpoints [I] de 06 (lista do ADR-0033): exige `Idempotency-Key` e guarda a resposta.
[AttributeUsage(AttributeTargets.Method)]
public sealed class IdempotentAttribute : Attribute, IFilterFactory
{
    public bool IsReusable => false;

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider) =>
        ActivatorUtilities.CreateInstance<IdempotencyFilter>(serviceProvider);
}

/// 07 §7.4. Abre a transação antes da ação (o decorator transacional a reutiliza), registra a chave,
/// e só confirma se a ação terminar em 2xx, junto com a resposta guardada. Erro → rollback: nada fica
/// gravado e o reenvio executa de novo.
internal sealed class IdempotencyFilter(
    AppDbContext db,
    IIdempotencyStore store,
    ISecretHasher hasher,
    IOptions<JsonOptions> json) : IAsyncActionFilter
{
    public const string KeyHeader = "Idempotency-Key";
    public const string ReplayedHeader = "Idempotent-Replayed";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;
        var header = http.Request.Headers[KeyHeader].ToString();
        if (string.IsNullOrWhiteSpace(header))
        {
            throw new BusinessException(ErrorCodes.IdempotencyKeyRequired);
        }

        if (!Guid.TryParse(header, out var key))
        {
            throw new BusinessException(ErrorCodes.MalformedRequest, "Idempotency-Key deve ser um UUID.");
        }

        var cancellationToken = http.RequestAborted;
        var endpoint = $"{http.Request.Method} {http.Request.Path}";

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var begin = await store.TryBeginAsync(key, endpoint, RequestHash(context), cancellationToken);
        if (begin is IdempotencyBegin.Replay replay)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            http.Response.Headers[ReplayedHeader] = "true";
            context.Result = new ContentResult
            {
                StatusCode = replay.Response.Status,
                Content = replay.Response.Body,
                ContentType = "application/json; charset=utf-8",
            };
            return;
        }

        var executed = await next();

        if (executed is { Exception: null, Result: ObjectResult result } && (result.StatusCode ?? StatusCodes.Status200OK) is >= 200 and < 300)
        {
            var status = result.StatusCode ?? StatusCodes.Status200OK;
            var body = result.Value is null ? null : JsonSerializer.Serialize(result.Value, result.Value.GetType(), json.Value.JsonSerializerOptions);
            var resourceId = (result.Value as IHasResourceId)?.ResourceId;

            // Requisição já encerrada não cancela a gravação: a operação aconteceu e precisa do registro.
            await store.CompleteAsync(key, status, body, resourceId, CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
            return;
        }

        await transaction.RollbackAsync(CancellationToken.None);
    }

    /// SHA-256 do corpo já desserializado e reserializado com as convenções da API: espaços e ordem
    /// das chaves não mudam o hash; qualquer valor diferente muda (ADR-0033).
    private string RequestHash(ActionExecutingContext context)
    {
        var bodies = context.ActionDescriptor.Parameters
            .Where(p => p.BindingInfo?.BindingSource == BindingSource.Body)
            .Select(p => context.ActionArguments.TryGetValue(p.Name, out var value) ? value : null)
            .ToList();

        var canonical = string.Join('\n', bodies.Select(b => b is null ? "null" : JsonSerializer.Serialize(b, b.GetType(), json.Value.JsonSerializerOptions)));
        return hasher.Hash(canonical);
    }
}
