using FluentValidation;
using StockAccount.Application.Common.Paging;
using StockAccount.Application.Common.Validation;
using StockAccount.Application.Features.Companies.Dtos;

namespace StockAccount.Application.Features.Companies.Validators;

public sealed class CompanySaveRequestValidator : AbstractValidator<CompanySaveRequest>
{
    public CompanySaveRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Firma adı zorunludur.")
            .MaximumLength(200).WithMessage("Firma adı en fazla 200 karakter olabilir.");
        RuleFor(x => x.TaxNumber).NotEmpty().WithMessage("Vergi numarası zorunludur.")
            .Must(t => ValidationHelpers.BeValidTaxNumber(t?.Trim())).WithMessage("Vergi numarası 10-11 haneli olmalıdır.");
        RuleFor(x => x.TaxOffice).MaxLen(100);
        RuleFor(x => x.Phone).MaxLen(20);
        RuleFor(x => x.Email).MaxLen(256)
            .EmailAddress().When(x => !string.IsNullOrWhiteSpace(x.Email)).WithMessage("Geçerli bir e-posta adresi giriniz.");
        RuleFor(x => x.Address).MaxLen(500);
        RuleFor(x => x.City).MaxLen(100);
    }
}

public sealed class CompanyListQueryValidator : PagedRequestValidator<CompanyListQuery>
{
    public CompanyListQueryValidator() => ValidateSort(CompanyService.Sorts);
}
