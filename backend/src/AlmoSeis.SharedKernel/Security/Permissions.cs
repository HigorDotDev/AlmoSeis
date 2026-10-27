namespace AlmoSeis.SharedKernel.Security;

/// Permissões do ADR-0028, com o complemento de leitura (orders.view, production.view).
public static class Permissions
{
    public static class Orders
    {
        public const string View = "orders.view";
        public const string Create = "orders.create";
        public const string Edit = "orders.edit";
        public const string Cancel = "orders.cancel";
        public const string EditDeliveryFee = "orders.delivery-fee.edit";
    }

    public static class Production
    {
        public const string View = "production.view";
        public const string Update = "production.update";
    }

    public static class Payments
    {
        public const string Register = "payments.register";
        public const string Reverse = "payments.reverse";
    }

    public static class Cash
    {
        public const string Open = "cash.open";
        public const string Movement = "cash.movement";
    }

    public static class Closing
    {
        public const string View = "closing.view";
        public const string Finalize = "closing.finalize";
        public const string Adjust = "closing.adjust";
    }

    public const string MenuManage = "menu.manage";
    public const string CatalogManage = "catalog.manage";
    public const string CustomersManage = "customers.manage";
    public const string EmployeesManage = "employees.manage";
    public const string DevicesManage = "devices.manage";
    public const string SettingsManage = "settings.manage";
    public const string AuditView = "audit.view";
    public const string SubscriptionView = "subscription.view";

    public static IReadOnlyList<string> All { get; } =
    [
        Orders.View, Orders.Create, Orders.Edit, Orders.Cancel, Orders.EditDeliveryFee,
        Production.View, Production.Update,
        Payments.Register, Payments.Reverse,
        Cash.Open, Cash.Movement,
        Closing.View, Closing.Finalize, Closing.Adjust,
        MenuManage, CatalogManage, CustomersManage,
        EmployeesManage, DevicesManage, SettingsManage, AuditView, SubscriptionView,
    ];
}

public enum Role
{
    Owner,
    Manager,
    Attendant,
    Kitchen,
    Cashier,
}

/// Matriz perfil × permissão de 06 §6.2. Alterar aqui exige alterar o 06 e o teste correspondente.
public static class RolePermissions
{
    private static readonly IReadOnlyDictionary<Role, IReadOnlySet<string>> Map = new Dictionary<Role, IReadOnlySet<string>>
    {
        [Role.Owner] = Permissions.All.ToHashSet(),
        [Role.Manager] = Permissions.All
            .Except([Permissions.EmployeesManage, Permissions.SubscriptionView])
            .ToHashSet(),
        [Role.Attendant] = new HashSet<string>
        {
            Permissions.Orders.View, Permissions.Orders.Create, Permissions.Orders.Edit, Permissions.CustomersManage,
        },
        [Role.Kitchen] = new HashSet<string>
        {
            Permissions.Orders.View, Permissions.Production.View, Permissions.Production.Update,
        },
        [Role.Cashier] = new HashSet<string>
        {
            Permissions.Orders.View, Permissions.Payments.Register,
            Permissions.Cash.Open, Permissions.Cash.Movement,
            Permissions.Closing.View, Permissions.Closing.Finalize,
            Permissions.CustomersManage,
        },
    };

    public static IReadOnlySet<string> For(Role role) => Map[role];

    public static IReadOnlySet<string> For(IEnumerable<Role> roles) =>
        roles.SelectMany(r => Map[r]).ToHashSet();

    /// Nome do perfil na API/banco: OWNER, MANAGER...
    public static string ToApiName(this Role role) => role.ToString().ToUpperInvariant();

    public static Role FromApiName(string name) => Enum.Parse<Role>(name, ignoreCase: true);
}
