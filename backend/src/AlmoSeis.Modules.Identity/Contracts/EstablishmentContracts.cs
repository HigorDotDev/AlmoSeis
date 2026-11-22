using AlmoSeis.SharedKernel.Security;

namespace AlmoSeis.Modules.Identity.Contracts;

// ---------- Comportamentos, rótulos e módulos (EP-003 fatia 1, ADR-0054; 06 §6.3 e §6.4) ----------

/// Rótulo informativo, lista fixa (ADR-0054 item 8). **Nenhuma regra de negócio lê rótulo** (item 7): o que muda o
/// funcionamento é o comportamento, via módulos (`ITenantModules`). O comportamento de cada rótulo e o texto em pt-BR
/// ficam em `EstablishmentLabels`.
public enum EstablishmentLabel
{
    Restaurant,
    Marmitaria,
    SelfService,
    BurgerJoint,
    PastelShop,
    AcaiShop,
    Pizzeria,
    SnackBar,
    IceCreamShop,
    Cafe,
    Bakery,
    SkewerBar,
    Sushi,
    SweetsShop,
    Other,
}

/// A lista fixa do ADR-0054 item 8, em código e coberta por teste. O app tem a mesma lista (sem rota de leitura).
public static class EstablishmentLabels
{
    public sealed record Entry(EstablishmentLabel Label, EstablishmentBehavior Behavior, string Text);

    public static IReadOnlyList<Entry> All { get; } =
    [
        new(EstablishmentLabel.Restaurant, EstablishmentBehavior.Restaurant, "Restaurante"),
        new(EstablishmentLabel.Marmitaria, EstablishmentBehavior.Restaurant, "Marmitaria"),
        new(EstablishmentLabel.SelfService, EstablishmentBehavior.Restaurant, "Self-service/quilo"),
        new(EstablishmentLabel.BurgerJoint, EstablishmentBehavior.SnackBar, "Hamburgueria"),
        new(EstablishmentLabel.PastelShop, EstablishmentBehavior.SnackBar, "Pastelaria"),
        new(EstablishmentLabel.AcaiShop, EstablishmentBehavior.SnackBar, "Açaiteria"),
        new(EstablishmentLabel.Pizzeria, EstablishmentBehavior.SnackBar, "Pizzaria"),
        new(EstablishmentLabel.SnackBar, EstablishmentBehavior.SnackBar, "Lanchonete"),
        new(EstablishmentLabel.IceCreamShop, EstablishmentBehavior.SnackBar, "Sorveteria"),
        new(EstablishmentLabel.Cafe, EstablishmentBehavior.SnackBar, "Cafeteria"),
        new(EstablishmentLabel.Bakery, EstablishmentBehavior.SnackBar, "Padaria"),
        new(EstablishmentLabel.SkewerBar, EstablishmentBehavior.SnackBar, "Espetaria"),
        new(EstablishmentLabel.Sushi, EstablishmentBehavior.SnackBar, "Sushi/temaki"),
        new(EstablishmentLabel.SweetsShop, EstablishmentBehavior.SnackBar, "Doceria"),
        new(EstablishmentLabel.Other, EstablishmentBehavior.SnackBar, "Outro"),
    ];

    private static readonly IReadOnlyDictionary<EstablishmentLabel, Entry> ByLabel = All.ToDictionary(e => e.Label);

    public static EstablishmentBehavior BehaviorOf(EstablishmentLabel label) => ByLabel[label].Behavior;

    public static string TextOf(EstablishmentLabel label) => ByLabel[label].Text;

    /// Código na API e no banco (`UPPER_SNAKE`): `SELF_SERVICE`, `ICE_CREAM_SHOP`.
    public static string ToApiName(EstablishmentLabel label) => System.Text.Json.JsonNamingPolicy.SnakeCaseUpper.ConvertName(label.ToString());

    /// Em ordem da lista fixa.
    public static IReadOnlyList<EstablishmentLabel> Ordered(IEnumerable<EstablishmentLabel> labels)
    {
        var set = labels.ToHashSet();
        return All.Select(e => e.Label).Where(set.Contains).ToList();
    }
}

/// POST /admin/tenants/{id}/behaviors (06 §6.15, ADR-0054 item 5).
public sealed record AddTenantBehaviorRequest(EstablishmentBehavior? Behavior, string? Reason);

/// Rota de plataforma: o tenant vem da rota e o administrador, do token de admin (ADR-0043). O handler assume a sessão
/// do tenant (`ITenantContext.InitializeForPlatformAdmin`) para gravar com a RLS dele.
public sealed record AddTenantBehaviorCommand(Guid AdminUserId, Guid TenantId, AddTenantBehaviorRequest Request);

public sealed record TenantBehaviorsDto(
    Guid TenantId,
    IReadOnlyList<EstablishmentBehavior> Behaviors,
    IReadOnlyList<EstablishmentLabel> Labels,
    IReadOnlyList<TenantModule> Modules);
