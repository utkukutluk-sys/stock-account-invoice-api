namespace StockAccount.Application.Common.Exceptions;

/// <summary>Base type for application errors that map to a ProblemDetails response with a contract error code.</summary>
public abstract class AppException : Exception
{
    protected AppException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }

    public virtual int StatusCode => ErrorCodes.StatusFor(Code);
}

/// <summary>404 NOT_FOUND (also used for rows of another tenant, CONTRACT §3.8).</summary>
public sealed class NotFoundException : AppException
{
    public NotFoundException(string message = "Kayıt bulunamadı.")
        : base(ErrorCodes.NotFound, message)
    {
    }

    public static NotFoundException For(string entityLabel) => new($"{entityLabel} bulunamadı.");
}

/// <summary>409 conflicts (duplicate keys, wrong status, concurrency, delete rules).</summary>
public sealed class ConflictException : AppException
{
    public ConflictException(string code, string message)
        : base(code, message)
    {
    }

    public override int StatusCode => 409;
}

/// <summary>422 business rule violations.</summary>
public sealed class BusinessRuleException : AppException
{
    public BusinessRuleException(string code, string message)
        : base(code, message)
    {
    }

    public override int StatusCode => 422;
}

/// <summary>403 FORBIDDEN raised by application code (policies normally handle this earlier).</summary>
public sealed class ForbiddenException : AppException
{
    public ForbiddenException(string message = "Bu işlem için yetkiniz yok.")
        : base(ErrorCodes.Forbidden, message)
    {
    }

    public override int StatusCode => 403;
}

/// <summary>401 authentication failures (INVALID_CREDENTIALS, ACCOUNT_LOCKED, USER_INACTIVE, INVALID_REFRESH_TOKEN).</summary>
public sealed class AuthenticationFailedException : AppException
{
    public AuthenticationFailedException(string code, string message)
        : base(code, message)
    {
    }

    public override int StatusCode => 401;
}

/// <summary>400 errors that are not field validation failures (e.g. COMPANY_REQUIRED).</summary>
public sealed class BadRequestException : AppException
{
    public BadRequestException(string code, string message)
        : base(code, message)
    {
    }

    public override int StatusCode => 400;
}
