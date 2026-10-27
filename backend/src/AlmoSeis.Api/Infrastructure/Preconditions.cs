using System.Globalization;
using AlmoSeis.Infrastructure.Concurrency;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Errors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace AlmoSeis.Api.Infrastructure;

/// Ações [M] de 06: exige `If-Match: "{version}"` e o entrega ao handler via IConcurrencyContext (ADR-0034).
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresIfMatchAttribute : ActionFilterAttribute
{
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var header = context.HttpContext.Request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(header))
        {
            throw new BusinessException(ErrorCodes.IfMatchRequired);
        }

        if (!TryParseVersion(header, out var version))
        {
            throw new BusinessException(ErrorCodes.MalformedRequest, "If-Match deve ser a versão entre aspas, ex.: \"3\".");
        }

        context.HttpContext.RequestServices.GetRequiredService<IConcurrencyContext>().SetExpectedVersion(version);
    }

    public static bool TryParseVersion(string header, out int version)
    {
        version = 0;
        var value = header.Trim();
        return value.Length >= 3
            && value[0] == '"'
            && value[^1] == '"'
            && int.TryParse(value.AsSpan(1, value.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture, out version)
            && version > 0;
    }
}

/// Toda resposta com recurso versionado leva `ETag: "{version}"` (06 §6.0).
internal sealed class ETagResultFilter : IResultFilter
{
    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Result is ObjectResult { Value: IVersionedDto dto })
        {
            context.HttpContext.Response.Headers.ETag = $"\"{dto.Version}\"";
        }
    }

    public void OnResultExecuted(ResultExecutedContext context)
    {
    }
}
