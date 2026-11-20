using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Realtime;
using AlmoSeis.Modules.Identity.Contracts;
using AlmoSeis.Modules.Notifications.Application;
using AlmoSeis.Modules.Notifications.Infrastructure;
using AlmoSeis.Modules.Notifications.Jobs;
using AlmoSeis.SharedKernel.Application;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AlmoSeis.Modules.Notifications;

/// Push para pedidos da página (ADR-0049): registro do token por instalação, envio pela Expo a partir do outbox, lembrete
/// agendado, recibos e limpeza. Fica acima do Ordering (07 §7.1): lê o estado do aceite pela porta
/// `IWebOrderAcceptanceReader` e as permissões pelo `IEmployeeAccessReader`; o Identity o chama pela
/// `IDeviceAccessListener` no logout, na troca de estabelecimento e nas revogações.
public static class NotificationsModule
{
    public static IServiceCollection AddNotificationsModule(this IServiceCollection services)
    {
        services.AddSingleton<IModuleModelConfiguration, NotificationsModelConfiguration>();
        services.AddScoped<IDeviceAccessListener, PushRegistrationCleaner>();
        services.AddScoped<WebOrderPushService>();
        services.AddScoped<PushReceiptChecker>();

        services.AddOptions<PushOptions>().BindConfiguration(PushOptions.Section).ValidateOnStart();
        services.AddSingleton<IValidateOptions<PushOptions>, PushOptionsValidator>();
        services.AddSingleton<FakePushSender>();
        services.AddSingleton<NoPushSender>();
        // O corpo leva os tokens: o cliente HTTP não registra log de requisição.
        services.AddHttpClient<ExpoPushSender>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All })
            .RemoveAllLoggers();
        services.AddTransient<IPushSender>(sp => sp.GetRequiredService<IOptions<PushOptions>>().Value.Provider switch
        {
            PushProvider.Expo => sp.GetRequiredService<ExpoPushSender>(),
            PushProvider.Fake => sp.GetRequiredService<FakePushSender>(),
            _ => sp.GetRequiredService<NoPushSender>(),
        });

        services.AddSingleton<IOutboxConsumer, WebOrderPushConsumer>();
        services.AddSingleton<PushMaintenanceJob>();
        services.AddHostedService(sp => sp.GetRequiredService<PushMaintenanceJob>());

        services.Scan(scan => scan
            .FromAssemblyOf<WebOrderPushService>()
            .AddClasses(c => c.AssignableTo(typeof(ICommandHandler<,>)), publicOnly: false).AsImplementedInterfaces().WithScopedLifetime()
            .AddClasses(c => c.AssignableTo(typeof(IQueryHandler<,>)), publicOnly: false).AsImplementedInterfaces().WithScopedLifetime());

        services.AddValidatorsFromAssemblyContaining<WebOrderPushService>(includeInternalTypes: true);
        return services;
    }
}
