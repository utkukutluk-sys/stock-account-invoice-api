using FluentValidation;
using StockAccount.Application.Common.Identity;
using StockAccount.Application.Common.Paging;
using StockAccount.Application.Features.Users.Dtos;

namespace StockAccount.Application.Features.Users.Validators;

public static class UserRules
{
    /// <summary>Password policy (CONTRACT §4.1): min 8, upper, lower, digit.</summary>
    public static IRuleBuilderOptions<T, string?> ApplyPasswordRules<T>(this IRuleBuilder<T, string?> rule) =>
        rule.NotEmpty().WithMessage("Şifre zorunludur.")
            .MinimumLength(8).WithMessage("Şifre en az 8 karakter olmalıdır.")
            .MaximumLength(100).WithMessage("Şifre en fazla 100 karakter olabilir.")
            .Must(p => p is not null && p.Any(char.IsUpper)).WithMessage("Şifre en az bir büyük harf içermelidir.")
            .Must(p => p is not null && p.Any(char.IsLower)).WithMessage("Şifre en az bir küçük harf içermelidir.")
            .Must(p => p is not null && p.Any(char.IsDigit)).WithMessage("Şifre en az bir rakam içermelidir.");

    public static bool IsValidRole(string? role) => role is Roles.Admin or Roles.User;
}

public sealed class UserCreateRequestValidator : AbstractValidator<UserCreateRequest>
{
    public UserCreateRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().WithMessage("E-posta zorunludur.")
            .MaximumLength(256).WithMessage("E-posta en fazla 256 karakter olabilir.")
            .EmailAddress().WithMessage("Geçerli bir e-posta adresi giriniz.");
        RuleFor(x => x.FullName).NotEmpty().WithMessage("Ad soyad zorunludur.")
            .MaximumLength(150).WithMessage("Ad soyad en fazla 150 karakter olabilir.");
        RuleFor(x => x.Password).ApplyPasswordRules();
        RuleFor(x => x.Role).Must(UserRules.IsValidRole).WithMessage("Rol 'Admin' veya 'User' olmalıdır.");
        RuleFor(x => x.CompanyId).NotNull().When(x => x.Role == Roles.User).WithMessage("Kullanıcı rolü için firma zorunludur.");
        RuleFor(x => x.CompanyId).Null().When(x => x.Role == Roles.Admin).WithMessage("Yönetici bir firmaya bağlanamaz.");
        RuleFor(x => x.CurrentAccountId).Null().When(x => x.Role == Roles.Admin).WithMessage("Yönetici bir cari hesaba bağlanamaz.");
    }
}

public sealed class UserUpdateRequestValidator : AbstractValidator<UserUpdateRequest>
{
    public UserUpdateRequestValidator()
    {
        RuleFor(x => x.FullName).NotEmpty().WithMessage("Ad soyad zorunludur.")
            .MaximumLength(150).WithMessage("Ad soyad en fazla 150 karakter olabilir.");
        RuleFor(x => x.Role).Must(UserRules.IsValidRole).WithMessage("Rol 'Admin' veya 'User' olmalıdır.");
        RuleFor(x => x.CompanyId).NotNull().When(x => x.Role == Roles.User).WithMessage("Kullanıcı rolü için firma zorunludur.");
        RuleFor(x => x.CompanyId).Null().When(x => x.Role == Roles.Admin).WithMessage("Yönetici bir firmaya bağlanamaz.");
        RuleFor(x => x.CurrentAccountId).Null().When(x => x.Role == Roles.Admin).WithMessage("Yönetici bir cari hesaba bağlanamaz.");
        RuleFor(x => x.NewPassword).ApplyPasswordRules().When(x => !string.IsNullOrEmpty(x.NewPassword));
    }
}

public sealed class UserListQueryValidator : PagedRequestValidator<UserListQuery>
{
    public UserListQueryValidator()
    {
        ValidateSort(UserService.Sorts);
        RuleFor(x => x.Role).Must(UserRules.IsValidRole).When(x => !string.IsNullOrEmpty(x.Role))
            .WithMessage("Rol 'Admin' veya 'User' olmalıdır.");
    }
}
