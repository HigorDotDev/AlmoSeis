using AlmoSeis.Infrastructure.Persistence;
using AlmoSeis.Modules.Payments.Domain;
using Microsoft.EntityFrameworkCore;

namespace AlmoSeis.Modules.Payments.Infrastructure;

/// Mapeamento de 05 §5.10.
internal sealed class PaymentsModelConfiguration : IModuleModelConfiguration
{
    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Payment>(b =>
        {
            b.ToTable("payments");
            b.HasKey(x => x.Id);
            b.Property(x => x.Label).HasMaxLength(30);
        });

        modelBuilder.Entity<PaymentReversal>(b =>
        {
            b.ToTable("payment_reversals");
            b.HasKey(x => x.Id);
            b.Property(x => x.Reason).HasMaxLength(300);
            // Relação "de ordenação" (07 §7.13): o estorno nunca é inserido antes do pagamento.
            b.HasOne<Payment>().WithMany().HasForeignKey(x => x.PaymentId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
