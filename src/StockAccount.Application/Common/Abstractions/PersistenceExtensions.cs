using Microsoft.EntityFrameworkCore;
using StockAccount.Application.Common.Exceptions;
using StockAccount.Domain.Common;

namespace StockAccount.Application.Common.Abstractions;

/// <summary>SaveChanges helpers that translate EF/DB failures into contract errors.</summary>
public static class PersistenceExtensions
{
    public const string ConcurrencyMessage = "Kayıt başka bir kullanıcı tarafından değiştirildi. Lütfen sayfayı yenileyip tekrar deneyin.";

    /// <summary>
    /// Saves; <see cref="DbUpdateConcurrencyException"/> → 409 CONCURRENCY_CONFLICT; unique violations → the
    /// exception produced by <paramref name="onUniqueViolation"/> (index name passed in, best effort).
    /// </summary>
    public static async Task SaveOrConflictAsync(
        this IApplicationDbContext db,
        IDatabaseErrorInspector inspector,
        Func<string?, AppException> onUniqueViolation,
        CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException(ErrorCodes.ConcurrencyConflict, ConcurrencyMessage);
        }
        catch (DbUpdateException ex) when (inspector.IsUniqueViolation(ex, out var indexName))
        {
            throw onUniqueViolation(indexName);
        }
    }

    /// <summary>Uses the client rowVersion as the original value so the UPDATE is guarded by it (DB_NOTES §2.4).</summary>
    public static void UseClientRowVersion<T>(this IApplicationDbContext db, T entity, byte[] rowVersion)
        where T : class, IHasRowVersion =>
        db.Entry(entity).Property(e => e.RowVersion).OriginalValue = rowVersion;
}
