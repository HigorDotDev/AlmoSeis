using System.Diagnostics;
using System.Text.Json;
using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Errors;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AlmoSeis.Api.Infrastructure;

/// Único ponto de conversão exceção → RFC 7807 com errorCode e traceId (06 §6.1, 07 §7.4).
public sealed class ProblemDetailsExceptionHandler(
    IProblemDetailsService problemDetails,
    ILogger<ProblemDetailsExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (code, detail, extensions) = exception switch
        {
            BusinessException business => (business.ErrorCode, business.Detail, business.Extensions),
            ValidationException validation => (ErrorCodes.ValidationFailed, (string?)null, ValidationExtensions(validation)),
            BadHttpRequestException or JsonException => (ErrorCodes.MalformedRequest, (string?)null, Empty),
            _ => (null, null, Empty),
        };

        if (code is null)
        {
            logger.LogError(exception, "Erro não tratado.");
            httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return await problemDetails.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                ProblemDetails = Build(httpContext, "urn:almoseis:error:internal", "Algo deu errado.", 500, null, "InternalError", Empty),
            });
        }

        if (exception is VersionConflictException conflict)
        {
            extensions = new Dictionary<string, object?>(extensions) { ["current"] = await ReadCurrentAsync(httpContext, conflict, cancellationToken) };
        }

        httpContext.Response.StatusCode = code.HttpStatus;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = Build(httpContext, code.TypeUri, code.Title, code.HttpStatus, detail, code.Code, extensions),
        });
    }

    /// O estado atual, serializado como no GET, para o app mostrar a diferença (ADR-0034).
    private static async Task<object?> ReadCurrentAsync(HttpContext httpContext, VersionConflictException conflict, CancellationToken cancellationToken)
    {
        var reader = httpContext.RequestServices.GetServices<ICurrentResourceReader>()
            .FirstOrDefault(r => r.EntityType == conflict.EntityType);
        return reader is null ? null : await reader.ReadAsync(conflict.EntityId, cancellationToken);
    }

    private static readonly IReadOnlyDictionary<string, object?> Empty = new Dictionary<string, object?>();

    private static ProblemDetails Build(
        HttpContext context, string type, string title, int status, string? detail, string errorCode,
        IReadOnlyDictionary<string, object?> extensions)
    {
        var problem = new ProblemDetails
        {
            Type = type,
            Title = title,
            Status = status,
            Detail = detail,
            Instance = context.Request.Path,
        };
        problem.Extensions["errorCode"] = errorCode;
        problem.Extensions["traceId"] = Activity.Current?.Id ?? context.TraceIdentifier;
        foreach (var (key, value) in extensions)
        {
            problem.Extensions[key] = value;
        }

        return problem;
    }

    private static Dictionary<string, object?> ValidationExtensions(ValidationException validation) => new()
    {
        ["errors"] = validation.Errors
            .GroupBy(e => CamelCasePath(e.PropertyName))
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()),
    };

    /// "Tenant.DocumentNumber" → "tenant.documentNumber"; "Items[0].Quantity" → "items[0].quantity" (06 §6.1).
    private static string CamelCasePath(string propertyPath) =>
        string.Join('.', propertyPath.Split('.').Select(segment => JsonNamingPolicy.CamelCase.ConvertName(segment)));
}

public sealed class DatabaseHealthCheck(PlatformDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        await db.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy()
            : HealthCheckResult.Unhealthy("Banco indisponível.");
}
