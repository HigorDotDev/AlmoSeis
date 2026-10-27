using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Modules.Billing.Application;
using AlmoSeis.Modules.Billing.Contracts;
using AlmoSeis.Modules.Billing.Domain;
using AlmoSeis.Modules.Billing.Infrastructure;
using AlmoSeis.SharedKernel.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AlmoSeis.Modules.Billing;

public static class BillingModule
{
    public static IServiceCollection AddBillingModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<BillingOptions>().Bind(configuration.GetSection(BillingOptions.Section));
        services.AddSingleton<IModuleModelConfiguration, BillingModelConfiguration>();
        services.AddScoped<IBillingPort, BillingPort>();
        services.AddSingleton<ISubscriptionAccess, SubscriptionAccessCache>();
        services.AddScoped<IQueryHandler<GetSubscriptionQuery, SubscriptionDto>, GetSubscriptionQueryHandler>();
        services.AddScoped<IAsaasWebhookReceiver, AsaasWebhookReceiver>();
        services.AddScoped<SubscriptionChangeRecorder>();
        services.AddScoped<IPlatformAdminService, PlatformAdminService>();

        // Sem chave do Asaas (desenvolvimento, testes), o gateway não cria nada e só registra em log.
        services.AddHttpClient<AsaasGateway>();
        services.TryAddScoped<ISubscriptionBillingGateway>(sp => sp.GetRequiredService<AsaasGateway>());

        services.AddSingleton<BillingWebhookProcessor>();
        services.AddHostedService(sp => sp.GetRequiredService<BillingWebhookProcessor>());
        services.AddSingleton<SubscriptionLifecycleJob>();
        services.AddHostedService(sp => sp.GetRequiredService<SubscriptionLifecycleJob>());
        return services;
    }
}

internal sealed class BillingModelConfiguration : IModuleModelConfiguration
{
    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Subscription>(b =>
        {
            b.ToTable("subscriptions");
            b.HasKey(x => x.Id);
            b.Property(x => x.PlanCode).HasMaxLength(30);
            b.Property(x => x.GatewayCustomerId).HasMaxLength(64);
            b.Property(x => x.GatewaySubscriptionId).HasMaxLength(64);
            b.Ignore(x => x.CanWrite);
        });

        modelBuilder.Entity<TrialClaim>(b =>
        {
            b.ToTable("trial_claims");
            b.HasKey(x => x.ClaimKey);
            b.Property(x => x.ClaimKey).HasMaxLength(64).IsFixedLength();
        });

        modelBuilder.Entity<BillingWebhookEvent>(b =>
        {
            b.ToTable("billing_webhook_events");
            b.HasKey(x => x.Id);
            b.Property(x => x.Gateway).HasMaxLength(20);
            b.Property(x => x.GatewayEventId).HasMaxLength(100);
            b.Property(x => x.EventType).HasMaxLength(60);
            b.Property(x => x.Payload).HasColumnType("jsonb");
        });
    }
}
