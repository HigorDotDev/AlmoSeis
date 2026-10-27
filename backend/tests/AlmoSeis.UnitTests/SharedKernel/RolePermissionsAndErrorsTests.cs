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
            ["orders.view", "orders.create", "orders.edit", "customers.manage"], ignoreOrder: true);

    [Fact]
    public void Kitchen_matrix() =>
        RolePermissions.For(Role.Kitchen).ShouldBe(
            ["orders.view", "production.view", "production.update"], ignoreOrder: true);

    [Fact]
    public void Cashier_matrix() =>
        RolePermissions.For(Role.Cashier).ShouldBe(
            ["orders.view", "payments.register", "cash.open", "cash.movement", "closing.view", "closing.finalize", "customers.manage"],
            ignoreOrder: true);

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
