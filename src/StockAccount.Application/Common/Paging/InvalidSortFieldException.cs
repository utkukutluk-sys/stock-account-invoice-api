namespace StockAccount.Application.Common.Paging;

/// <summary>Thrown by <see cref="QueryableSortingExtensions.ApplySort{T}"/> for a field outside the whitelist.
/// The global exception handler should map it to 400 VALIDATION_FAILED with errors["sort"].</summary>
public sealed class InvalidSortFieldException : ArgumentException
{
    public InvalidSortFieldException(string field)
        : base($"Geçersiz sıralama alanı: {field}", "sort")
    {
        Field = field;
    }

    public string Field { get; }
}
