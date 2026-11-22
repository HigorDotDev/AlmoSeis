using AlmoSeis.SharedKernel.Errors;

namespace AlmoSeis.SharedKernel.Security;

/// Comportamento do estabelecimento (ADR-0054 itens 1 a 6). O tenant tem um conjunto; é o que decide os módulos.
/// Fica no SharedKernel, como as permissões, porque todos os módulos perguntam pelos módulos do tenant.
public enum EstablishmentBehavior
{
    Restaurant,
    SnackBar,
}

/// Módulo do tenant (ADR-0054 item 12). Não é persistido: sai do mapa fixo por comportamento.
public enum TenantModule
{
    Marmita,
    Pf,
    Kilo,
    DailyMenu,
    ALaCarte,
    Delivery,
    FixedMenu,
}

/// Mapa fixo comportamento → módulos (ADR-0054 item 12). Módulos efetivos = união dos módulos de cada comportamento.
public static class TenantModuleMap
{
    private static readonly IReadOnlyDictionary<EstablishmentBehavior, TenantModule[]> Map = new Dictionary<EstablishmentBehavior, TenantModule[]>
    {
        [EstablishmentBehavior.Restaurant] = [TenantModule.Marmita, TenantModule.Pf, TenantModule.Kilo, TenantModule.DailyMenu],
        [EstablishmentBehavior.SnackBar] = [TenantModule.ALaCarte, TenantModule.Delivery, TenantModule.FixedMenu],
    };

    public static IReadOnlyList<TenantModule> For(EstablishmentBehavior behavior) => Map[behavior];

    /// Em ordem do enum. Conjunto vazio = tenant criado pela API anterior na janela do deploy da 0008: vale `RESTAURANT`
    /// (05 §5.17 0008, decisão do usuário em 2026-11-22).
    public static IReadOnlyList<TenantModule> For(IEnumerable<EstablishmentBehavior> behaviors)
    {
        var set = EffectiveBehaviors(behaviors).SelectMany(b => Map[b]).ToHashSet();
        return Enum.GetValues<TenantModule>().Where(set.Contains).ToList();
    }

    /// Comportamentos em ordem do enum, com o `RESTAURANT` da janela do deploy quando não há nenhuma linha.
    public static IReadOnlyList<EstablishmentBehavior> EffectiveBehaviors(IEnumerable<EstablishmentBehavior> behaviors)
    {
        var set = behaviors.ToHashSet();
        return set.Count == 0
            ? [EstablishmentBehavior.Restaurant]
            : Enum.GetValues<EstablishmentBehavior>().Where(set.Contains).ToList();
    }
}

/// "O tenant tem o módulo X?" (ADR-0054 item 13). Toda checagem de código pergunta por módulo, **nunca** pelo
/// comportamento nem pelo rótulo. Implementado pelo Identity, sobre `tenant_behaviors` do tenant da sessão.
public interface ITenantModules
{
    Task<IReadOnlyList<TenantModule>> GetAsync(CancellationToken cancellationToken);

    Task<bool> HasAsync(TenantModule module, CancellationToken cancellationToken);

    /// Sem o módulo → `403 ModuleNotEnabled` com a extensão `module` (ADR-0054 item 14).
    async Task EnsureAsync(TenantModule module, CancellationToken cancellationToken)
    {
        if (!await HasAsync(module, cancellationToken))
        {
            throw ModuleNotEnabled(module);
        }
    }

    static BusinessException ModuleNotEnabled(TenantModule module) =>
        new(ErrorCodes.ModuleNotEnabled, extensions: new Dictionary<string, object?> { ["module"] = ModuleNames.ToApiName(module) });
}

/// Nome na API (`UPPER_SNAKE`, igual ao JSON): `A_LA_CARTE`, `DAILY_MENU`.
public static class ModuleNames
{
    public static string ToApiName(TenantModule module) => System.Text.Json.JsonNamingPolicy.SnakeCaseUpper.ConvertName(module.ToString());

    public static string ToApiName(EstablishmentBehavior behavior) => System.Text.Json.JsonNamingPolicy.SnakeCaseUpper.ConvertName(behavior.ToString());
}
