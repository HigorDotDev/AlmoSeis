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
        // Jobs de plataforma (`*.Jobs`) só acham candidatos entre tenants; a escrita é no contexto do tenant (07 §7.12).
        var result = Types.InAssemblies(ModuleAssemblies)
            .That().DoNotResideInNamespaceMatching(@"^AlmoSeis\.Modules\.((Billing|Sync)\.|\w+\.Jobs$)")
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
            .Where(t => !typeof(IDomainEvent).IsAssignableFrom(t)) // eventos carregam o tenant, mas não são gravados
            .Select(t => t.FullName!)
            .Except(TenantRuleExceptions)
            .ToList();

        offenders.ShouldBeEmpty();
    }

    /// ADR-0053 §3.4: cookie de sessão só é lido depois da checagem de `Origin`. A API inteira lê cookie da requisição
    /// num único lugar, `WebSession.ReadCookie`, que chama `EnsureAllowedOrigin` antes; qualquer outra leitura
    /// (`Request.Cookies`, header `Cookie`) numa ação, filtro ou middleware falha aqui. Escrever cookie
    /// (`Response.Cookies.Append/Delete`) é livre.
    [Fact]
    public void Rule8_request_cookies_are_read_only_through_the_origin_checked_helper()
    {
        var src = FindRepoDirectory("backend", "src");
        var read = new Regex(@"\.Cookies\b(?!\s*\.\s*(Append|Delete)\b)|\bHeaders\s*\.\s*Cookie\b|\bHeaderNames\s*\.\s*Cookie\b|\[\s*""Cookie""\s*\]|\bIRequestCookieCollection\b|\bRequestCookieCollection\b");
        var helperFile = Path.Combine("AlmoSeis.Api", "Infrastructure", "WebSession.cs");

        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                         && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            var text = File.ReadAllText(file).ReplaceLineEndings("\n");
            var matches = read.Matches(text);
            if (matches.Count == 0)
            {
                continue;
            }

            if (!file.EndsWith(helperFile, StringComparison.Ordinal))
            {
                offenders.Add($"{Path.GetRelativePath(src, file)}: {string.Join(", ", matches.Select(m => m.Value))}");
                continue;
            }

            // No WebSession, toda leitura fica dentro do ReadCookie, e o ReadCookie confere o Origin.
            var start = text.IndexOf("static string? ReadCookie(", StringComparison.Ordinal);
            var end = start < 0 ? -1 : text.IndexOf("\n    }\n", start, StringComparison.Ordinal);
            if (start < 0 || end < 0 || !text[start..end].Contains("EnsureAllowedOrigin(request);", StringComparison.Ordinal))
            {
                offenders.Add($"{helperFile}: ReadCookie ausente ou sem EnsureAllowedOrigin");
                continue;
            }

            offenders.AddRange(matches.Where(m => m.Index < start || m.Index > end).Select(m => $"{helperFile}: '{m.Value}' fora do ReadCookie"));
        }

        offenders.ShouldBeEmpty("Leia cookie só por WebSession.RefreshToken/DeviceSecret (ADR-0053 §3.4, 07 §7.12).");
    }

    private static string Failing(NetArchTest.Rules.TestResult result) =>
        result.IsSuccessful ? string.Empty : "Violações: " + string.Join(", ", result.FailingTypeNames ?? []);

    /// Sobe a partir da saída do build e, se ela estiver fora do repositório (`--artifacts-path`), a partir deste arquivo-fonte.
    private static string FindRepoDirectory(params string[] parts) =>
        FindUp(AppContext.BaseDirectory, parts) ?? FindUp(Path.GetDirectoryName(SourceFile())!, parts)
        ?? throw new DirectoryNotFoundException(string.Join('/', parts));

    private static string? FindUp(string start, string[] parts)
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null && !Directory.Exists(Path.Combine([dir.FullName, .. parts])))
        {
            dir = dir.Parent;
        }

        return dir is null ? null : Path.Combine([dir.FullName, .. parts]);
    }

    private static string SourceFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
}
