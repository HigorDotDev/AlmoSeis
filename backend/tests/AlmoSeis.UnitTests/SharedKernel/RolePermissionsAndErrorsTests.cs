using AlmoSeis.SharedKernel.Domain;
using AlmoSeis.SharedKernel.Errors;
using AlmoSeis.SharedKernel.Security;
using Shouldly;

namespace AlmoSeis.UnitTests.SharedKernel;

/// A matriz precisa bater exatamente com 06 §6.2. Mudou lá → muda aqui (e vice-versa).
public sealed class RolePermissionsTests
{
    [Fact]
    public void Owner_has_everything() =>
        RolePermissions.For(Role.Owner).ShouldBe(Permissions.All.ToHashSet(), ignoreOrder: true);

    [Fact]
    public void Manager_has_everything_except_employees_and_subscription()
    {
        var manager = RolePermissions.For(Role.Manager);
        manager.ShouldNotContain(Permissions.EmployeesManage);
        manager.ShouldNotContain(Permissions.SubscriptionView);
        manager.Count.ShouldBe(Permissions.All.Count - 2);
    }

    [Fact]
    public void Attendant_matrix() =>
        RolePermissions.For(Role.Attendant).ShouldBe(
            ["orders.view", "orders.create", "orders.edit", "customers.manage", "orders.accept", "orders.fulfillment.update", "catalog.availability"], ignoreOrder: true);

    [Fact]
    public void Kitchen_matrix() =>
        RolePermissions.For(Role.Kitchen).ShouldBe(
            ["orders.view", "production.view", "production.update", "orders.fulfillment.update", "catalog.availability"], ignoreOrder: true);

    [Fact]
    public void Cashier_matrix() =>
        RolePermissions.For(Role.Cashier).ShouldBe(
            ["orders.view", "payments.register", "cash.open", "cash.movement", "closing.view", "closing.finalize", "customers.manage",
             "orders.accept", "orders.fulfillment.update"],
            ignoreOrder: true);

    /// ADR-0046 item 22: aceite para quem atende (não a cozinha); atendimento para todos os perfis.
    [Fact]
    public void Accept_and_fulfillment_follow_adr_0046()
    {
        foreach (var role in Enum.GetValues<Role>())
        {
            var permissions = RolePermissions.For(role);
            permissions.Contains(Permissions.Orders.Accept).ShouldBe(role != Role.Kitchen, role.ToString());
            permissions.Contains(Permissions.Orders.UpdateFulfillment).ShouldBeTrue(role.ToString());
        }
    }

    /// EP-003 Q-306: pausar e reativar o cardápio fixo para quem está no balcão e na cozinha; editar continua com catalog.manage.
    [Fact]
    public void Catalog_availability_follows_q306()
    {
        foreach (var role in Enum.GetValues<Role>())
        {
            var permissions = RolePermissions.For(role);
            permissions.Contains(Permissions.CatalogAvailability).ShouldBe(role != Role.Cashier, role.ToString());
            permissions.Contains(Permissions.CatalogManage).ShouldBe(role is Role.Owner or Role.Manager, role.ToString());
        }
    }

    [Fact]
    public void Multiple_roles_are_the_union() =>
        RolePermissions.For([Role.Attendant, Role.Cashier]).ShouldContain(Permissions.Payments.Register);

    [Fact]
    public void Only_owner_and_manager_reverse_cancel_and_adjust()
    {
        foreach (var role in Enum.GetValues<Role>())
        {
            var allowed = role is Role.Owner or Role.Manager;
            var permissions = RolePermissions.For(role);
            permissions.Contains(Permissions.Payments.Reverse).ShouldBe(allowed, role.ToString());
            permissions.Contains(Permissions.Orders.Cancel).ShouldBe(allowed, role.ToString());
            permissions.Contains(Permissions.Closing.Adjust).ShouldBe(allowed, role.ToString());
        }
    }
}

public sealed class ErrorCodesTests
{
    [Fact]
    public void Codes_are_unique() =>
        ErrorCodes.All.Select(c => c.Code).ShouldBeUnique();

    [Fact]
    public void Type_uri_is_kebab_case() =>
        ErrorCodes.OrderAlreadyPaid.TypeUri.ShouldBe("urn:almoseis:error:order-already-paid");

    [Fact]
    public void Version_conflict_carries_versions()
    {
        var ex = new VersionConflictException("ORDER", Guid.Empty, 4, 5);
        ex.ErrorCode.HttpStatus.ShouldBe(409);
        ex.Extensions["expectedVersion"].ShouldBe(4);
        ex.Extensions["currentVersion"].ShouldBe(5);
    }
}

public sealed class AggregateRootTests
{
    private sealed class Sample : AggregateRoot, ISyncTracked
    {
        public string SyncEntityType => "SAMPLE";

        public void Change() => Touch();
    }

    [Fact]
    public void Touch_increments_version_once_per_unit_of_work()
    {
        var sample = new Sample();
        sample.Change();
        sample.Change();
        sample.Version.ShouldBe(2);

        sample.ClearDomainEvents();
        sample.Change();
        sample.Version.ShouldBe(3);
    }

    [Fact]
    public void EnsureVersion_throws_conflict_with_entity_type()
    {
        var sample = new Sample();
        var ex = Should.Throw<VersionConflictException>(() => sample.EnsureVersion(7));
        ex.EntityType.ShouldBe("SAMPLE");
        ex.CurrentVersion.ShouldBe(1);
    }
}
