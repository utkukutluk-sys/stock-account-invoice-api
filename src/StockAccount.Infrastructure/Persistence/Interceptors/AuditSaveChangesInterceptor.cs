using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using StockAccount.Application.Common.Abstractions;
using StockAccount.Domain.Common;
using StockAccount.Domain.Entities;

namespace StockAccount.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Sets audit fields, converts deletes of <see cref="ISoftDelete"/> entities into soft deletes, and
/// rejects updates/deletes of ledger rows (CONTRACT §2.1). <see cref="ICurrentUser"/> is optional:
/// when it is not registered or there is no user (seeding/background), user columns stay null.
/// </summary>
public sealed class AuditSaveChangesInterceptor : SaveChangesInterceptor
{
    private readonly ICurrentUser? _currentUser;
    private readonly TimeProvider _timeProvider;

    public AuditSaveChangesInterceptor(ICurrentUser? currentUser = null, TimeProvider? timeProvider = null)
    {
        _currentUser = currentUser;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Apply(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Apply(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Apply(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var userId = _currentUser?.UserId;

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.Entity is StockTrans or ActTrans && entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new InvalidOperationException(
                    $"{entry.Metadata.ClrType.Name} is an immutable ledger: rows can only be inserted (write reversal rows instead).");
            }

            switch (entry.State)
            {
                case EntityState.Added:
                    if (entry.Entity is IHasCreatedAt created && created.CreatedAt == default)
                    {
                        created.CreatedAt = now;
                    }

                    if (entry.Entity is ICreationAudited creationAudited)
                    {
                        creationAudited.CreatedByUserId ??= userId;
                    }

                    break;

                case EntityState.Modified:
                    SetUpdated(entry.Entity, now, userId);
                    break;

                case EntityState.Deleted when entry.Entity is ISoftDelete softDelete:
                    entry.State = EntityState.Modified;
                    softDelete.IsDeleted = true;
                    softDelete.DeletedAt = now;
                    softDelete.DeletedByUserId = userId;
                    SetUpdated(entry.Entity, now, userId);
                    break;
            }
        }
    }

    private static void SetUpdated(object entity, DateTime now, int? userId)
    {
        if (entity is IAuditable auditable)
        {
            auditable.UpdatedAt = now;
            auditable.UpdatedByUserId = userId;
        }
    }
}
