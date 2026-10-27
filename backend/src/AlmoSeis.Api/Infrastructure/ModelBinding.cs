using System.Text.Json;
using AlmoSeis.SharedKernel.Errors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace AlmoSeis.Api.Infrastructure;

/// Enums em query/rota no mesmo formato do JSON: `?kind=MAIN_DISH` (06 §6.0).
internal sealed class UpperSnakeEnumModelBinderProvider : IModelBinderProvider
{
    public IModelBinder? GetBinder(ModelBinderProviderContext context)
    {
        var type = Nullable.GetUnderlyingType(context.Metadata.ModelType) ?? context.Metadata.ModelType;
        return type.IsEnum && context.BindingInfo.BindingSource != BindingSource.Body ? new Binder(type) : null;
    }

    private sealed class Binder(Type enumType) : IModelBinder
    {
        private readonly Dictionary<string, object> _values = Enum.GetValues(enumType).Cast<object>()
            .ToDictionary(v => JsonNamingPolicy.SnakeCaseUpper.ConvertName(v.ToString()!), v => v, StringComparer.Ordinal);

        public Task BindModelAsync(ModelBindingContext bindingContext)
        {
            var raw = bindingContext.ValueProvider.GetValue(bindingContext.ModelName).FirstValue;
            if (string.IsNullOrEmpty(raw))
            {
                return Task.CompletedTask;
            }

            if (_values.TryGetValue(raw, out var value))
            {
                bindingContext.Result = ModelBindingResult.Success(value);
            }
            else
            {
                bindingContext.ModelState.AddModelError(bindingContext.ModelName, $"Valor desconhecido. Use: {string.Join(", ", _values.Keys)}.");
            }

            return Task.CompletedTask;
        }
    }
}

internal static class ModelBindingSetup
{
    /// Model state inválido (JSON quebrado, enum ou tipo errado) → 400 MalformedRequest no formato de 06 §6.1,
    /// no lugar do ValidationProblemDetails padrão do [ApiController].
    public static void UseAlmoSeisModelStateResponse(this ApiBehaviorOptions options) =>
        options.InvalidModelStateResponseFactory = context => throw new BusinessException(
            ErrorCodes.MalformedRequest,
            extensions: new Dictionary<string, object?>
            {
                ["errors"] = context.ModelState
                    .Where(e => e.Value?.Errors.Count > 0)
                    .ToDictionary(
                        e => JsonNamingPolicy.CamelCase.ConvertName(e.Key.TrimStart('$', '.')),
                        e => e.Value!.Errors.Select(x => string.IsNullOrEmpty(x.ErrorMessage) ? "Valor inválido." : x.ErrorMessage).ToArray()),
            });
}
