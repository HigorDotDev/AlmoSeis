using AlmoSeis.Api.Controllers.Public;
using AlmoSeis.Api.Infrastructure;
using AlmoSeis.Infrastructure;
using AlmoSeis.Infrastructure.Realtime;
using AlmoSeis.Modules.Audit;
using AlmoSeis.Modules.Billing;
using AlmoSeis.Modules.Catalog;
using AlmoSeis.Modules.Closing;
using AlmoSeis.Modules.Identity;
using AlmoSeis.Modules.Menu;
using AlmoSeis.Modules.Notifications;
using AlmoSeis.Modules.Ordering;
using AlmoSeis.Modules.Payments;
using AlmoSeis.Modules.Storefront;
using AlmoSeis.Modules.Sync;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

// Configuração: appsettings + variáveis de ambiente (`Secao__Chave`) + user-secrets; Key Vault só com `KeyVault:Uri` (ADR-0060).
builder.Configuration.AddAlmoSeisKeyVault();
// OpenTelemetry só com `Otel:Endpoint` e/ou a connection string do Application Insights (ADR-0060).
builder.AddAlmoSeisTelemetry();

builder.Services.AddAlmoSeisInfrastructure(builder.Configuration);
builder.Services.AddBillingModule(builder.Configuration);
builder.Services.AddIdentityModule();
builder.Services.AddCatalogModule();
builder.Services.AddMenuModule();
builder.Services.AddClosingModule();
builder.Services.AddStorefrontModule();
builder.Services.AddOrderingModule();
builder.Services.AddPaymentsModule();
builder.Services.AddNotificationsModule();
builder.Services.AddSyncModule();
builder.Services.AddAuditModule();
// Demais módulos entram aqui à medida que forem implementados (07 §7.5), sempre antes do decorator.
builder.Services.DecorateCommandHandlers();

builder.Services.AddHttpContextAccessor();
builder.Services.AddAlmoSeisAuthentication();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ProblemDetailsExceptionHandler>();
builder.Services.AddScoped<ValidationFilter>();
builder.Services.AddScoped<SubscriptionWriteGuardFilter>();
builder.Services.AddSingleton<WebSessionCookieFilter>();
builder.Services.AddScoped<PlaceWebOrderPreparation>();
builder.Services.AddRestaurantAppCors();
builder.Services.AddPublicPageCors();
builder.Services.AddAlmoSeisForwardedHeaders();
builder.Services
    .AddControllers(o =>
    {
        o.Filters.AddService<SubscriptionWriteGuardFilter>();
        o.Filters.AddService<ValidationFilter>();
        o.Filters.Add<ETagResultFilter>();
        o.Filters.AddService<WebSessionCookieFilter>();
        o.ModelBinderProviders.Insert(0, new UpperSnakeEnumModelBinderProvider());
    })
    .ConfigureApiBehaviorOptions(o => o.UseAlmoSeisModelStateResponse())
    .AddJsonOptions(o => ApiJson.Configure(o.JsonSerializerOptions));
// ProblemDetails (inclusive o `current` do 409) usa as opções de JSON do HTTP, não as do MVC.
builder.Services.ConfigureHttpJsonOptions(o => ApiJson.Configure(o.SerializerOptions));
builder.Services.AddSignalR().AddJsonProtocol(o => ApiJson.Configure(o.PayloadSerializerOptions));
builder.Services.AddSingleton<IRealtimePublisher, SignalRRealtimePublisher>();
builder.Services.AddOptions<AppVersionOptions>().BindConfiguration(AppVersionOptions.Section);
builder.Services.AddOptions<RateLimitSettings>().BindConfiguration(RateLimitSettings.Section);
builder.Services.AddAlmoSeisRateLimiting();
// `/health/live` não consulta nada (o processo responde); `/health/ready` abre conexão com o banco, com tempo-limite curto
// para o healthcheck do Compose não ficar pendurado quando o PostgreSQL some. O outbox atrasado deixa o `ready` `Degraded`
// (200, corpo `Degraded`): o monitor externo por palavra-chave avisa, nada é reiniciado (07 §7.4, 09 §9.8).
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"], timeout: TimeSpan.FromSeconds(3))
    .AddCheck<OutboxHealthCheck>("outbox", failureStatus: HealthStatus.Degraded, tags: ["ready"], timeout: TimeSpan.FromSeconds(3));

var app = builder.Build();
app.WarnAboutIgnoredTelemetrySettings();

// Primeiro: o IP e o esquema reais (proxy confiável de ForwardedHeaders) valem para o log de erro, o HSTS e o rate limit.
app.UseForwardedHeaders();
app.UseExceptionHandler();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

// CORS logo depois do tratador de erros: o preflight responde aqui, e os erros também levam os headers (ADR-0053 §4).
// Duas políticas que não se misturam: a página pública (sem credenciais) só em /api/v1/public; o app do restaurante no resto.
app.UseWhen(PublicRoutes.IsPublic, branch => branch.UseCors(PublicPageCorsOptions.Policy));
app.UseWhen(http => !PublicRoutes.IsPublic(http), branch => branch.UseCors(RestaurantAppCorsOptions.Policy));
app.UseMiddleware<HubOriginMiddleware>();
app.UseMiddleware<AppVersionMiddleware>();
app.UseMiddleware<RateLimitKeyMiddleware>();
app.UseRateLimiter();
// Rotas públicas: tenant pelo slug ou pelo token, antes da autenticação (que as ignora, ADR-0045).
app.UseMiddleware<TrackingHeadersMiddleware>();
app.UseMiddleware<PublicTenantMiddleware>();
// Cota por estabelecimento das cotações de entrega: só depois de o slug virar tenant (ADR-0045 item 11).
app.UseMiddleware<PublicStoreQuotaMiddleware>();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
// O app renova o token no accessTokenFactory antes de reconectar (08 §8.6); sem isto, uma aba do desktop
// ficaria conectada com o JWT vencido (ADR-0053).
app.MapHub<RealtimeHub>("/hubs/realtime", o => o.CloseOnAuthenticationExpiration = true);
app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new() { Predicate = c => c.Tags.Contains("ready") });

app.Run();

public partial class Program;
