using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using AlmoSeis.Api.Infrastructure;
using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Modules.Identity.Contracts;
using AlmoSeis.Modules.Identity.Domain;
using AlmoSeis.Modules.Ordering.Application;
using AlmoSeis.Modules.Ordering.Contracts;
using AlmoSeis.SharedKernel.Domain;
using AlmoSeis.SharedKernel.Errors;
using AlmoSeis.SharedKernel.Security;
using Shouldly;

namespace AlmoSeis.UnitTests.Modules;

/// Comportamentos, rótulos e módulos (EP-003 fatia 1, ADR-0054).
public sealed class EstablishmentBehaviorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Module_map_is_the_table_of_adr_0054()
    {
        Names(TenantModuleMap.For(EstablishmentBehavior.Restaurant)).ShouldBe(["MARMITA", "PF", "KILO", "DAILY_MENU"]);
        Names(TenantModuleMap.For(EstablishmentBehavior.SnackBar)).ShouldBe(["A_LA_CARTE", "DELIVERY", "FIXED_MENU"]);
        Names(TenantModuleMap.For([EstablishmentBehavior.SnackBar, EstablishmentBehavior.Restaurant]))
            .ShouldBe(["MARMITA", "PF", "KILO", "DAILY_MENU", "A_LA_CARTE", "DELIVERY", "FIXED_MENU"]);
    }

    /// Janela do deploy da 0008: tenant sem linha em tenant_behaviors é restaurante (decisão do usuário, 07 §7.13).
    [Fact]
    public void Tenant_without_behavior_rows_is_a_restaurant()
    {
        TenantModuleMap.EffectiveBehaviors([]).ShouldBe([EstablishmentBehavior.Restaurant]);
        Names(TenantModuleMap.For([])).ShouldBe(["MARMITA", "PF", "KILO", "DAILY_MENU"]);
    }

    [Fact]
    public void Api_names_match_the_ddl_checks()
    {
        Enum.GetValues<EstablishmentBehavior>().Select(ModuleNames.ToApiName).ShouldBe(["RESTAURANT", "SNACK_BAR"]);
        Enum.GetValues<EstablishmentLabel>().Select(l => UpperSnakeEnumConverter<EstablishmentLabel>.ToText(l)).ShouldBe(
        [
            "RESTAURANT", "MARMITARIA", "SELF_SERVICE", "BURGER_JOINT", "PASTEL_SHOP", "ACAI_SHOP", "PIZZERIA", "SNACK_BAR",
            "ICE_CREAM_SHOP", "CAFE", "BAKERY", "SKEWER_BAR", "SUSHI", "SWEETS_SHOP", "OTHER",
        ]);
        UpperSnakeEnumConverter<TenantModule>.ToText(TenantModule.ALaCarte).ShouldBe("A_LA_CARTE");
        Enum.GetValues<EstablishmentLabel>().Select(EstablishmentLabels.ToApiName)
            .ShouldBe(Enum.GetValues<EstablishmentLabel>().Select(l => UpperSnakeEnumConverter<EstablishmentLabel>.ToText(l)));
    }

    /// ADR-0054 item 8: a lista fixa, com o comportamento e o texto de cada rótulo.
    [Fact]
    public void Label_list_is_the_fixed_list_of_adr_0054()
    {
        EstablishmentLabels.All.Select(e => e.Label).ShouldBe(Enum.GetValues<EstablishmentLabel>());
        EstablishmentLabels.All.Where(e => e.Behavior == EstablishmentBehavior.Restaurant).Select(e => e.Text)
            .ShouldBe(["Restaurante", "Marmitaria", "Self-service/quilo"]);
        EstablishmentLabels.All.Where(e => e.Behavior == EstablishmentBehavior.SnackBar).Select(e => e.Text).ShouldBe(
        [
            "Hamburgueria", "Pastelaria", "Açaiteria", "Pizzaria", "Lanchonete", "Sorveteria", "Cafeteria", "Padaria",
            "Espetaria", "Sushi/temaki", "Doceria", "Outro",
        ]);
    }

    [Fact]
    public void Signup_takes_exactly_one_behavior_and_labels_of_it()
    {
        var tenant = NewTenant(EstablishmentBehavior.SnackBar, EstablishmentLabel.AcaiShop, EstablishmentLabel.BurgerJoint);

        tenant.Behaviors.ShouldHaveSingleItem().Origin.ShouldBe(BehaviorOrigin.Signup);
        tenant.EffectiveLabels.ShouldBe([EstablishmentLabel.BurgerJoint, EstablishmentLabel.AcaiShop]);
        Names(tenant.Modules).ShouldBe(["A_LA_CARTE", "DELIVERY", "FIXED_MENU"]);
        tenant.Version.ShouldBe(1);
    }

    [Fact]
    public void Signup_refuses_no_label_and_labels_of_another_behavior()
    {
        ValidationField(() => NewTenant(EstablishmentBehavior.Restaurant)).ShouldBe("tenant.labels");
        ValidationField(() => NewTenant(EstablishmentBehavior.Restaurant, EstablishmentLabel.Marmitaria, EstablishmentLabel.BurgerJoint))
            .ShouldBe("tenant.labels");
    }

    [Fact]
    public void Replacing_labels_keeps_the_version_when_nothing_changes()
    {
        var tenant = NewTenant(EstablishmentBehavior.Restaurant, EstablishmentLabel.Marmitaria);

        tenant.ReplaceLabels([EstablishmentLabel.Marmitaria], Now).ShouldBeFalse();
        tenant.Version.ShouldBe(1);

        tenant.ReplaceLabels([EstablishmentLabel.SelfService, EstablishmentLabel.Restaurant], Now).ShouldBeTrue();
        tenant.Version.ShouldBe(2);
        tenant.EffectiveLabels.ShouldBe([EstablishmentLabel.Restaurant, EstablishmentLabel.SelfService]);
        Names(tenant.Modules).ShouldBe(["MARMITA", "PF", "KILO", "DAILY_MENU"]); // rótulo não muda funcionamento
    }

    [Fact]
    public void Replacing_labels_refuses_empty_and_labels_of_a_behavior_not_enabled()
    {
        var tenant = NewTenant(EstablishmentBehavior.Restaurant, EstablishmentLabel.Marmitaria);

        ValidationField(() => tenant.ReplaceLabels([], Now)).ShouldBe("labels");
        ValidationField(() => tenant.ReplaceLabels([EstablishmentLabel.Pizzeria], Now)).ShouldBe("labels");
        tenant.EffectiveLabels.ShouldBe([EstablishmentLabel.Marmitaria]);
    }

    [Fact]
    public void Platform_admin_adds_a_behavior_once_and_raises_modules_changed()
    {
        var admin = Guid.CreateVersion7();
        var tenant = NewTenant(EstablishmentBehavior.Restaurant, EstablishmentLabel.Marmitaria);

        tenant.AddBehavior(EstablishmentBehavior.SnackBar, admin, Now).ShouldBeTrue();

        tenant.EffectiveBehaviors.ShouldBe([EstablishmentBehavior.Restaurant, EstablishmentBehavior.SnackBar]);
        var added = tenant.Behaviors.Single(b => b.Behavior == EstablishmentBehavior.SnackBar);
        (added.Origin, added.EnabledByUserId).ShouldBe((BehaviorOrigin.PlatformAdmin, admin));
        var changed = tenant.DomainEvents.OfType<TenantModulesChanged>().ShouldHaveSingleItem();
        changed.Modules.Count.ShouldBe(7);
        tenant.Version.ShouldBe(2);

        // Agora o rótulo da lanchonete vale.
        tenant.ReplaceLabels([EstablishmentLabel.Marmitaria, EstablishmentLabel.BurgerJoint], Now).ShouldBeTrue();

        tenant.ClearDomainEvents();
        tenant.AddBehavior(EstablishmentBehavior.SnackBar, admin, Now).ShouldBeFalse();
        tenant.DomainEvents.ShouldBeEmpty();
    }

    /// Tenant da janela do deploy: o acréscimo grava antes o `RESTAURANT` que ele já tinha por regra (origem SYSTEM).
    [Fact]
    public void Adding_a_behavior_to_a_tenant_of_the_deploy_window_keeps_the_restaurant()
    {
        var tenant = (Tenant)RuntimeHelpers.GetUninitializedObject(typeof(Tenant));
        typeof(Tenant).GetField("_behaviors", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(tenant, new List<TenantBehavior>());
        typeof(Tenant).GetField("_labels", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(tenant, new List<TenantLabel>());
        typeof(AggregateRoot).GetField("_domainEvents", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(tenant, new List<IDomainEvent>());

        tenant.EffectiveBehaviors.ShouldBe([EstablishmentBehavior.Restaurant]);
        tenant.EffectiveLabels.ShouldBe([EstablishmentLabel.Restaurant]);

        tenant.AddBehavior(EstablishmentBehavior.SnackBar, Guid.CreateVersion7(), Now).ShouldBeTrue();

        tenant.Behaviors.Select(b => (b.Behavior, b.Origin)).ShouldBe(
            [(EstablishmentBehavior.Restaurant, BehaviorOrigin.System), (EstablishmentBehavior.SnackBar, BehaviorOrigin.PlatformAdmin)]);
        tenant.Behaviors[0].EnabledByUserId.ShouldBeNull();
        tenant.Labels.ShouldHaveSingleItem().Label.ShouldBe(EstablishmentLabel.Restaurant);
    }

    [Theory]
    [InlineData(OrderType.Marmita, TenantModule.Marmita)]
    [InlineData(OrderType.Pf, TenantModule.Pf)]
    [InlineData(OrderType.Kilo, TenantModule.Kilo)]
    public void Each_order_type_belongs_to_a_module(OrderType orderType, TenantModule module) =>
        OrderRules.ModuleOf(orderType).ShouldBe(module);

    [Fact]
    public async Task Missing_module_is_403_with_the_module_extension()
    {
        ITenantModules modules = new FixedModules([TenantModule.ALaCarte, TenantModule.Delivery, TenantModule.FixedMenu]);

        await modules.EnsureAsync(TenantModule.FixedMenu, CancellationToken.None);
        var error = await Should.ThrowAsync<BusinessException>(() => modules.EnsureAsync(TenantModule.DailyMenu, CancellationToken.None));

        error.ErrorCode.ShouldBe(ErrorCodes.ModuleNotEnabled);
        error.ErrorCode.HttpStatus.ShouldBe(403);
        error.Extensions["module"].ShouldBe("DAILY_MENU");
    }

    [Fact]
    public void Modules_changed_goes_to_everyone_in_the_tenant()
    {
        var tenantId = Guid.NewGuid();
        RealtimeGroups.ForEvent(tenantId, "TenantModulesChanged", new JsonObject()).ShouldBe([$"tenant:{tenantId}"]);
    }

    private static Tenant NewTenant(EstablishmentBehavior behavior, params EstablishmentLabel[] labels) =>
        Tenant.Create(
            Guid.CreateVersion7(), "Marmitaria da Ana", "CNPJ", "11222333000181", "Uberlândia", "MG", "America/Sao_Paulo",
            "2026-10", Guid.CreateVersion7(), behavior, labels, Now);

    private static string ValidationField(Action action)
    {
        var error = Should.Throw<BusinessException>(action);
        error.ErrorCode.ShouldBe(ErrorCodes.ValidationFailed);
        return ((IReadOnlyDictionary<string, string[]>)error.Extensions["errors"]!).Keys.Single();
    }

    private static List<string> Names(IEnumerable<TenantModule> modules) => modules.Select(ModuleNames.ToApiName).ToList();

    private sealed class FixedModules(IReadOnlyList<TenantModule> modules) : ITenantModules
    {
        public Task<IReadOnlyList<TenantModule>> GetAsync(CancellationToken cancellationToken) => Task.FromResult(modules);

        public Task<bool> HasAsync(TenantModule module, CancellationToken cancellationToken) => Task.FromResult(modules.Contains(module));
    }
}
