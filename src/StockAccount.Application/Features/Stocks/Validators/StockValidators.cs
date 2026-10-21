using FluentValidation;
using StockAccount.Application.Common.Paging;
using StockAccount.Application.Common.Validation;
using StockAccount.Application.Features.Stocks.Dtos;
using StockAccount.Domain.Common;

namespace StockAccount.Application.Features.Stocks.Validators;

public sealed class StockSaveRequestValidator : AbstractValidator<StockSaveRequest>
{
    public StockSaveRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().WithMessage("Stok kodu zorunludur.")
            .Must(ValidationHelpers.BeValidCode).WithMessage("Stok kodu yalnızca A-Z, 0-9, '.', '_' ve '-' içerebilir (en fazla 50 karakter).");
        RuleFor(x => x.Name).NotEmpty().WithMessage("Stok adı zorunludur.")
            .MaximumLength(200).WithMessage("Stok adı en fazla 200 karakter olabilir.");
        RuleFor(x => x.Unit).IsInEnum().WithMessage("Geçersiz birim.");
        RuleFor(x => x.Barcode).MaxLen(50);
        RuleFor(x => x.VatRate).Must(DomainRules.IsAllowedVatRate).WithMessage("KDV oranı 0, 1, 10 veya 20 olmalıdır.");
        RuleFor(x => x.UnitPrice).GreaterThanOrEqualTo(0).WithMessage("Birim fiyat negatif olamaz.")
            .Must(p => ValidationHelpers.HaveAtMostDecimals(p, 4)).WithMessage("Birim fiyat en fazla 4 ondalık basamak içerebilir.")
            .LessThan(100_000_000_000_000m).WithMessage("Birim fiyat çok büyük.");
        RuleFor(x => x.Description).MaxLen(500);
    }
}

public sealed class StockListQueryValidator : PagedRequestValidator<StockListQuery>
{
    public StockListQueryValidator()
    {
        ValidateSort(StockService.Sorts);
        RuleFor(x => x.Unit).IsInEnum().When(x => x.Unit.HasValue).WithMessage("Geçersiz birim.");
    }
}
