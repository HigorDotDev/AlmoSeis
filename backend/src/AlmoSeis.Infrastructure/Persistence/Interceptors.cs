using System.Data.Common;
using AlmoSeis.Infrastructure.Tenancy;
using AlmoSeis.SharedKernel.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AlmoSeis.Infrastructure.Persistence;

/// Define app.tenant_id/app.user_id na abertura de cada conexão (05 §5.2). O Npgsql faz DISCARD ALL
/// ao devolver a conexão ao pool, o que limpa as variáveis.
public sealed class TenantSessionConnectionInterceptor(ITenantContext tenant) : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = CreateCommand(connection);
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = CreateCommand(connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private DbCommand CreateCommand(DbConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = "SELECT set_config('app.tenant_id', @t, false), set_config('app.user_id', @u, false)";
        AddParameter(command, "t", tenant.TenantId?.ToString() ?? string.Empty);
        AddParameter(command, "u", tenant.UserId?.ToString() ?? string.Empty);
        return command;
    }

    private static void AddParameter(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

/// Primeira barreira de escrita: preenche TenantId em inserções e recusa gravação em outro tenant (ADR-0026).
public sealed class TenantGuardSaveChangesInterceptor(ITenantContext tenant) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Guard(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Guard(eventData.Context);
        return ValueTask.FromResult(result);
    }

    private void Guard(DbContext? context)
    {
        if (context is null || tenant.BypassTenantFilter)
        {
            return;
        }

        foreach (var entry in context.ChangeTracker.Entries<ITenantOwned>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            var tenantProperty = entry.Property(nameof(ITenantOwned.TenantId));
            if (entry.State == EntityState.Added && (Guid)tenantProperty.CurrentValue! == Guid.Empty && tenant.TenantId is { } current)
            {
                tenantProperty.CurrentValue = current;
            }

            if ((Guid)tenantProperty.CurrentValue! != tenant.TenantId)
            {
                throw new InvalidOperationException(
                    $"Tentativa de gravar {entry.Metadata.ClrType.Name} de outro tenant. Isso é um bug: o tenant vem só do token.");
            }
        }
    }
}
