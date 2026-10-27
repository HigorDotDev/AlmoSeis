using AlmoSeis.Api.Infrastructure;
using AlmoSeis.Infrastructure;
using AlmoSeis.Modules.Billing;
using AlmoSeis.Modules.Catalog;
using AlmoSeis.Modules.Identity;
using AlmoSeis.Modules.Menu;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAlmoSeisInfrastructure(builder.Configuration);
builder.Services.AddBillingModule();
builder.Services.AddIdentityModule();
builder.Services.AddCatalogModule();
builder.Services.AddMenuModule();
// Demais módulos entram aqui à medida que forem implementados (07 §7.5), sempre antes do decorator.
builder.Services.DecorateCommandHandlers();

builder.Services.AddHttpContextAccessor();
builder.Services.AddAlmoSeisAuthentication();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ProblemDetailsExceptionHandler>();
builder.Services.AddScoped<ValidationFilter>();
builder.Services
    .AddControllers(o =>
    {
        o.Filters.AddService<ValidationFilter>();
        o.Filters.Add<ETagResultFilter>();
        o.ModelBinderProviders.Insert(0, new UpperSnakeEnumModelBinderProvider());
    })
    .ConfigureApiBehaviorOptions(o => o.UseAlmoSeisModelStateResponse())
    .AddJsonOptions(o => ApiJson.Configure(o.JsonSerializerOptions));
// ProblemDetails (inclusive o `current` do 409) usa as opções de JSON do HTTP, não as do MVC.
builder.Services.ConfigureHttpJsonOptions(o => ApiJson.Configure(o.SerializerOptions));
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);

var app = builder.Build();

app.UseExceptionHandler();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new() { Predicate = c => c.Tags.Contains("ready") });

app.Run();

public partial class Program;
