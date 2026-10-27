using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Infrastructure.Security;
using AlmoSeis.Modules.Billing.Contracts;
using AlmoSeis.Modules.Billing.Domain;
using AlmoSeis.SharedKernel.Application;
using AlmoSeis.SharedKernel.Domain;
using AlmoSeis.SharedKernel.Errors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AlmoSeis.Modules.Billing
{
    public static class BillingModule
    {
        public static IServiceCollection AddBillingModule(this IServiceCollection services)
        {
            services.AddSingleton<IModuleModelConfiguration, BillingModelConfiguration>();
            services.AddScoped<IBillingPort, BillingPort>();
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
        }
    }

    internal sealed class BillingPort(AppDbContext db, IIdGenerator ids, ISecretHasher hasher, TimeProvider time) : IBillingPort
    {
        // Plano do piloto; preço real vem da configuração de Billing quando o gateway entrar (ADR-0017).
        private const string PilotPlan = "PILOT";

        public async Task StartTrialAsync(Guid tenantId, IReadOnlyCollection<string> claimSources, CancellationToken cancellationToken)
        {
            var keys = claimSources.Select(hasher.Hash).Distinct().ToList();
            if (await db.Set<TrialClaim>().AnyAsync(c => keys.Contains(c.ClaimKey), cancellationToken))
            {
                throw new BusinessException(ErrorCodes.TrialAlreadyClaimed);
            }

            var now = time.GetUtcNow();
            foreach (var key in keys)
            {
                db.Add(TrialClaim.Create(key, tenantId, now));
            }

            db.Add(Subscription.StartTrial(ids.New(), tenantId, PilotPlan, Money.Zero, now));
        }

        public async Task<SubscriptionSummary?> GetSummaryAsync(Guid tenantId, CancellationToken cancellationToken)
        {
            // No autocadastro a assinatura ainda está só no change tracker (o UnitOfWork grava no commit).
            var subscription = db.Set<Subscription>().Local.FirstOrDefault(s => s.TenantId == tenantId)
                ?? await db.Set<Subscription>().AsNoTracking().SingleOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);
            return subscription is null
                ? null
                : new SubscriptionSummary(
                    subscription.Status,
                    subscription.TrialEndsAt,
                    subscription.DaysLeft(time.GetUtcNow()),
                    subscription.CanWrite);
        }
    }
}

namespace AlmoSeis.Modules.Billing.Contracts
{
    public sealed record SubscriptionSummary(string Status, DateTimeOffset TrialEndsAt, int? DaysLeft, bool CanWrite);

    /// O que outros módulos podem pedir ao Billing (07 §7.7).
    public interface IBillingPort
    {
        /// Grava os TrialClaims (hash de documento/telefone) e a assinatura TRIALING. TrialAlreadyClaimed se repetido.
        Task StartTrialAsync(Guid tenantId, IReadOnlyCollection<string> claimSources, CancellationToken cancellationToken);

        Task<SubscriptionSummary?> GetSummaryAsync(Guid tenantId, CancellationToken cancellationToken);
    }
}
