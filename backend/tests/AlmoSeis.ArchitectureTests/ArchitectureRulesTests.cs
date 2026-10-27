using System.Reflection;
using System.Text.RegularExpressions;
using AlmoSeis.SharedKernel.Domain;
using NetArchTest.Rules;
using Shouldly;

namespace AlmoSeis.ArchitectureTests;

/// Regras de 07 §7.12. Cada regra cita o motivo; ao falhar, a mensagem lista os tipos culpados.
public sealed class ArchitectureRulesTests
{
    private static readonly Assembly[] ProductionAssemblies =
    [
        typeof(Entity).Assembly,                                         // SharedKernel
        typeof(AlmoSeis.Infrastructure.DependencyInjection).Assembly,    // Infrastructure
        .. Directory.GetFiles(AppContext.BaseDirectory, "AlmoSeis.Modules.*.dll").Select(Assembly.LoadFrom),
        typeof(Program).Assembly,                                        // Api
    ];

    private static IEnumerable<Assembly> ModuleAssemblies =>
        ProductionAssemblies.Where(a => a.GetName().Name!.StartsWith("AlmoSeis.Modules.", StringComparison.Ordinal));

    [Fact]
    public void Rule1_domain_does_not_depend_on_ef_aspnet_or_infrastructure()
    {
        var result = Types.InAssemblies(ProductionAssemblies)
            .That().ResideInNamespaceMatching(@"^AlmoSeis\.(SharedKernel|Modules\.\w+)\.Domain")
            .ShouldNot().HaveDependencyOnAny("Microsoft.EntityFrameworkCore", "Microsoft.AspNetCore", "AlmoSeis.Infrastructure")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Failing(result));
    }

    [Fact]
    public void Rule2_modules_only_use_contracts_of_other_modules()
    {
        foreach (var module in ModuleAssemblies)
        {
            var name = module.GetName().Name!;
            var others = ModuleAssemblies.Select(a => a.GetName().Name!).Where(n => n != name).ToArray();
            if (others.Length == 0)
            {
                continue;
            }

            var forbidden = others.SelectMany(o => new[] { o + ".Domain", o + ".Application", o + ".Infrastructure" }).ToArray();
            var result = Types.InAssembly(module).ShouldNot().HaveDependencyOnAny(forbidden).GetResult();
            result.IsSuccessful.ShouldBeTrue($"{name}: {Failing(result)}");
        }
    }

    [Fact]
    public void Rule3_controllers_do_not_touch_dbcontext_or_domain()
    {
        string[] forbidden =
        [
            "AlmoSeis.Infrastructure.Persistence",
            .. ModuleAssemblies.Select(a => a.GetName().Name + ".Domain"),
        ];

        var result = Types.InAssembly(typeof(Program).Assembly)
            .That().HaveNameEndingWith("Controller")
            .ShouldNot().HaveDependencyOnAny(forbidden)
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Failing(result));
    }

    [Fact]
    public void Rule4_no_direct_clock_access_in_source()
    {
        // Análise de fonte: mais simples e confiável que IL para este caso.
        var src = FindRepoDirectory("backend", "src");
        var offenders = Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"\bDateTime(Offset)?\.(Now|UtcNow|Today)\b"))
            .ToList();

        offenders.ShouldBeEmpty("Use TimeProvider injetado (02-padroes §2.2).");
    }

    [Fact]
    public void Rule5_platform_context_only_in_allowed_places()
    {
        var result = Types.InAssemblies(ModuleAssemblies)
            .That().DoNotResideInNamespaceMatching(@"^AlmoSeis\.Modules\.(Billing|Sync)\.")
            .ShouldNot().HaveDependencyOn("AlmoSeis.Infrastructure.Persistence.PlatformDbContext")
            .GetResult();

        result.IsSuccessful.ShouldBeTrue(Failing(result));
    }

    /// Tabelas sem RLS de tenant por decisão documentada (05 §5.12, grupo 4): ficam fora da regra.
    private static readonly string[] TenantRuleExceptions =
    [
        "AlmoSeis.Modules.Identity.Domain.RefreshToken", // identidade: acessada só pelo módulo Identity
        "AlmoSeis.Modules.Billing.Domain.TrialClaim",    // plataforma: hash de documento, sem dados de tenant
    ];

    [Fact]
    public void Rule6_domain_entities_with_tenant_id_implement_ITenantOwned()
    {
        var offenders = ProductionAssemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t.IsClass && !t.IsAbstract && t.Namespace?.EndsWith(".Domain", StringComparison.Ordinal) == true)
            .Where(t => t.GetProperty("TenantId")?.PropertyType == typeof(Guid))
            .Where(t => !typeof(ITenantOwned).IsAssignableFrom(t))
            .Select(t => t.FullName!)
            .Except(TenantRuleExceptions)
            .ToList();

        offenders.ShouldBeEmpty();
    }

    private static string Failing(NetArchTest.Rules.TestResult result) =>
        result.IsSuccessful ? string.Empty : "Violações: " + string.Join(", ", result.FailingTypeNames ?? []);

    private static string FindRepoDirectory(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine([dir.FullName, .. parts])))
        {
            dir = dir.Parent;
        }

        return dir is null ? throw new DirectoryNotFoundException(string.Join('/', parts)) : Path.Combine([dir.FullName, .. parts]);
    }
}
