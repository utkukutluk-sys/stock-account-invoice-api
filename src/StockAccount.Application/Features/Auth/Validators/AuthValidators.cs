using FluentValidation;
using StockAccount.Application.Features.Auth.Dtos;
using StockAccount.Application.Features.Users.Validators;

namespace StockAccount.Application.Features.Auth.Validators;

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().WithMessage("E-posta zorunludur.")
            .MaximumLength(256).WithMessage("E-posta en fazla 256 karakter olabilir.");
        RuleFor(x => x.Password).NotEmpty().WithMessage("Şifre zorunludur.");
    }
}

public sealed class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(x => x.CurrentPassword).NotEmpty().WithMessage("Mevcut şifre zorunludur.");
        RuleFor(x => x.NewPassword).ApplyPasswordRules();
    }
}
