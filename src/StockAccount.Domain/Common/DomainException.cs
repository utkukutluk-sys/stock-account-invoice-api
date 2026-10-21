namespace StockAccount.Domain.Common;

/// <summary>Business rule violation raised by the domain. <see cref="Code"/> maps to an HTTP status (CONTRACT §5.2).</summary>
public class DomainException : Exception
{
    public DomainException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }
}
