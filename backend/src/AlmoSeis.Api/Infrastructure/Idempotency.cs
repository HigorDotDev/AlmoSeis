using System.Text.Json;
using System.Text.Json.Nodes;
using AlmoSeis.Infrastructure.Idempotency;
using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Security;
using AlmoSeis.Infrastructure.Tenancy;
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

    /// Rota pública (ADR-0045 item 10): o `endpoint` gravado é o modelo da rota, não o caminho, para o reenvio depois de
    /// uma troca de slug (redirecionamento 308) continuar sendo a mesma operação. O escopo continua `(tenant, key)`.
    public bool Public { get; init; }

    /// Campo da resposta com um segredo do cliente (ex.: `trackingToken`, ADR-0047): sai do corpo guardado e é recomposto
    /// no reenvio pelo `IReplaySecretSource` com o mesmo `Field`.
    public string? ReplaySecret { get; init; }

    /// `IIdempotentPreparation` que roda **antes** da transação, depois de conferir que não é o reenvio de uma operação
    /// concluída: chamadas externas lentas (Turnstile, mapas) que não podem segurar a transação nem os advisory locks.
    public Type? Preparation { get; init; }

    public IFilterMetadata CreateInstance(IServiceProvider serviceProvider) =>
        ActivatorUtilities.CreateInstance<IdempotencyFilter>(serviceProvider, new IdempotencyRoute(Public, ReplaySecret, Preparation));
}

/// Passo de uma rota [I] fora da transação (07 §7.4). Não grava nada que precise do rollback da operação; o que ele
/// apurar vai para a ação (ex.: em `HttpContext.Items`), e os erros que ele encontrar são lançados pelo handler, na ordem
/// das checagens do contrato.
public interface IIdempotentPreparation
{
    Task PrepareAsync(ActionExecutingContext context, CancellationToken cancellationToken);
}

internal sealed record IdempotencyRoute(bool IsPublic, string? ReplaySecret, Type? Preparation);

/// 07 §7.4. Abre a transação antes da ação (o decorator transacional a reutiliza), registra a chave,
/// e só confirma se a ação terminar em 2xx, junto com a resposta guardada. Erro → rollback: nada fica
/// gravado e o reenvio executa de novo.
internal sealed class IdempotencyFilter(
    AppDbContext db,
    IIdempotencyStore store,
    ISecretHasher hasher,
    ITenantContext tenant,
    IEnumerable<IReplaySecretSource> secrets,
    IOptions<JsonOptions> json,
    IdempotencyRoute route) : IAsyncActionFilter
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
        var endpoint = route.IsPublic && context.ActionDescriptor.AttributeRouteInfo?.Template is { } template
            ? $"{http.Request.Method} /{template}"
            : $"{http.Request.Method} {http.Request.Path}";
        var requestHash = RequestHash(context);

        if (route.Preparation is { } preparation)
        {
            // Reenvio de operação concluída: responde já, sem repetir o Turnstile (token de uso único) nem o mapa.
            if (await store.FindCompletedAsync(key, endpoint, requestHash, cancellationToken) is { } completed)
            {
                Replay(context, completed);
                return;
            }

            await ((IIdempotentPreparation)http.RequestServices.GetRequiredService(preparation)).PrepareAsync(context, cancellationToken);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var begin = await store.TryBeginAsync(key, endpoint, requestHash, cancellationToken);
        if (begin is IdempotencyBegin.Replay replay)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            Replay(context, replay.Response);
            return;
        }

        var executed = await next();

        if (executed is { Exception: null, Result: ObjectResult result } && (result.StatusCode ?? StatusCodes.Status200OK) is >= 200 and < 300)
        {
            var status = result.StatusCode ?? StatusCodes.Status200OK;
            var body = result.Value is null ? null : JsonSerializer.Serialize(result.Value, result.Value.GetType(), json.Value.JsonSerializerOptions);
            var resourceId = (result.Value as IHasResourceId)?.ResourceId;
            if (body is not null && route.ReplaySecret is { } field)
            {
                body = WithoutField(body, field);
            }

            // Requisição já encerrada não cancela a gravação: a operação aconteceu e precisa do registro.
            await store.CompleteAsync(key, status, body, resourceId, CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
            return;
        }

        await transaction.RollbackAsync(CancellationToken.None);
    }

    private void Replay(ActionExecutingContext context, StoredResponse response)
    {
        context.HttpContext.Response.Headers[ReplayedHeader] = "true";
        context.Result = new ContentResult
        {
            StatusCode = response.Status,
            Content = route.ReplaySecret is { } field && response.Body is { } body ? WithField(body, field, response.ResourceId) : response.Body,
            ContentType = "application/json; charset=utf-8",
        };
    }

    private static string WithoutField(string body, string field)
    {
        var node = JsonNode.Parse(body)!.AsObject();
        node.Remove(field);
        return node.ToJsonString();
    }

    private string WithField(string body, string field, Guid? resourceId)
    {
        var source = secrets.Single(s => s.Field == field);
        var node = JsonNode.Parse(body)!.AsObject();
        if (node.ContainsKey(field))
        {
            // Registro gravado antes da 0007 (até 72 h): o token aleatório daquela época só existe ali.
            return body;
        }

        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Reenvio idempotente sem tenant.");
        var value = resourceId is { } id ? source.Recreate(tenantId, id) : throw new InvalidOperationException($"Reenvio sem resource_id para recompor {field}.");
        node.Insert(0, field, value);
        return node.ToJsonString(json.Value.JsonSerializerOptions);
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
