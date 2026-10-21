using FluentValidation;
using StockAccount.Application.Common.Paging;
using StockAccount.Application.Common.Validation;
using StockAccount.Application.Features.CurrentAccounts.Dtos;

namespace StockAccount.Application.Features.CurrentAccounts.Validators;

public sealed class CurrentAccountSaveRequestValidator : AbstractValidator<CurrentAccountSaveRequest>
{
    public CurrentAccountSaveRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().WithMessage("Cari kodu zorunludur.")
            .Must(ValidationHelpers.BeValidCode).WithMessage("Cari kodu yalnızca A-Z, 0-9, '.', '_' ve '-' içerebilir (en fazla 50 karakter).");
        RuleFor(x => x.Title).NotEmpty().WithMessage("Unvan zorunludur.")
            .MaximumLength(200).WithMessage("Unvan en fazla 200 karakter olabilir.");
        RuleFor(x => x.Type).IsInEnum().WithMessage("Geçersiz cari tipi.");
        RuleFor(x => x.TaxNumber).Must(t => ValidationHelpers.BeValidTaxNumber(t!.Trim()))
            .When(x => !string.IsNullOrWhiteSpace(x.TaxNumber)).WithMessage("Vergi numarası 10-11 haneli olmalıdır.");
        RuleFor(x => x.TaxOffice).MaxLen(100);
        RuleFor(x => x.Phone).MaxLen(20);
        RuleFor(x => x.Email).MaxLen(256)
            .EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.Email)).WithMessage("Geçerli bir e-posta adresi giriniz.");
        RuleFor(x => x.Address).MaxLen(500);
        RuleFor(x => x.City).MaxLen(100);
    }
}

public sealed class CurrentAccountListQueryValidator : PagedRequestValidator<CurrentAccountListQuery>
{
    public CurrentAccountListQueryValidator()
    {
        ValidateSort(CurrentAccountService.Sorts);
        RuleFor(x => x.Type).IsInEnum().When(x => x.Type.HasValue).WithMessage("Geçersiz cari tipi.");
        RuleFor(x => x.City).MaxLen(100);
    }
}
