using AlmoSeis.Api.Infrastructure;
using AlmoSeis.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddAlmoSeisInfrastructure(builder.Configuration);
// Módulos entram aqui à medida que forem implementados (07 §7.5): builder.Services.AddIdentityModule(); ...
builder.Services.DecorateCommandHandlers();

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ProblemDetailsExceptionHandler>();
builder.Services.AddControllers().AddJsonOptions(o => ApiJson.Configure(o.JsonSerializerOptions));
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);

var app = builder.Build();

app.UseExceptionHandler();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.MapControllers();
app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new() { Predicate = c => c.Tags.Contains("ready") });

app.Run();

public partial class Program;
