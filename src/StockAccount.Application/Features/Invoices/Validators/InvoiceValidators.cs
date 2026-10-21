using FluentValidation;
using StockAccount.Application.Common.Paging;
using StockAccount.Application.Common.Validation;
using StockAccount.Application.Features.Invoices.Dtos;
using StockAccount.Domain.Common;
using StockAccount.Domain.Enums;

namespace StockAccount.Application.Features.Invoices.Validators;

public sealed class InvoiceSaveRequestValidator : AbstractValidator<InvoiceSaveRequest>
{
    public InvoiceSaveRequestValidator()
    {
        RuleFor(x => x.Type).IsInEnum().When(x => x.Type.HasValue).WithMessage("Geçersiz fatura türü.");
        RuleFor(x => x.Series)
            .Must(s => DomainRules.IsValidSeries(DomainRules.NormalizeCode(s)))
            .When(x => !string.IsNullOrWhiteSpace(x.Series))
            .WithMessage("Seri 1-10 karakter, yalnızca harf ve rakam olmalıdır.");
        RuleFor(x => x.InvoiceDate).NotNull().WithMessage("Fatura tarihi zorunludur.");
        RuleFor(x => x.DueDate)
            .Must((x, due) => x.InvoiceDate is null || due >= x.InvoiceDate)
            .When(x => x.DueDate.HasValue)
            .WithMessage("Vade tarihi fatura tarihinden önce olamaz.");
        RuleFor(x => x.CurrentAccountId).GreaterThan(0).WithMessage("Cari hesap seçiniz.");
        RuleFor(x => x.Description).MaxLen(500);
        RuleFor(x => x.Lines).NotNull().WithMessage("Fatura satırları zorunludur.")
            .Must(l => l is { Count: > 0 }).WithMessage("Faturada en az bir satır olmalıdır.")
            .Must(l => l is null || l.Count <= DomainRules.MaxInvoiceLines)
            .WithMessage($"Faturada en fazla {DomainRules.MaxInvoiceLines} satır olabilir.");
        RuleForEach(x => x.Lines).SetValidator(new InvoiceLineRequestValidator());
    }
}

public sealed class InvoiceLineRequestValidator : AbstractValidator<InvoiceLineRequest>
{
    public InvoiceLineRequestValidator()
    {
        RuleFor(x => x.StockId).GreaterThan(0).WithMessage("Stok seçiniz.");
        RuleFor(x => x.Quantity).GreaterThan(0).WithMessage("Miktar 0'dan büyük olmalıdır.")
            .Must(q => ValidationHelpers.HaveAtMostDecimals(q, 3)).WithMessage("Miktar en fazla 3 ondalık basamak içerebilir.")
            .LessThan(1_000_000_000_000m).WithMessage("Miktar çok büyük.");
        RuleFor(x => x.UnitPrice).GreaterThanOrEqualTo(0).WithMessage("Birim fiyat negatif olamaz.")
            .Must(p => ValidationHelpers.HaveAtMostDecimals(p, 4)).WithMessage("Birim fiyat en fazla 4 ondalık basamak içerebilir.")
            .LessThan(100_000_000_000_000m).WithMessage("Birim fiyat çok büyük.");
        RuleFor(x => x.VatRate).Must(DomainRules.IsAllowedVatRate).WithMessage("KDV oranı 0, 1, 10 veya 20 olmalıdır.");
        RuleFor(x => x.Description).MaxLen(250);
    }
}

public sealed class InvoiceApproveRequestValidator : AbstractValidator<InvoiceApproveRequest>
{
    public InvoiceApproveRequestValidator() =>
        RuleFor(x => x.RowVersion).NotEmpty().WithMessage("rowVersion zorunludur.");
}

public sealed class InvoiceCancelRequestValidator : AbstractValidator<InvoiceCancelRequest>
{
    public InvoiceCancelRequestValidator()
    {
        RuleFor(x => x.RowVersion).NotEmpty().WithMessage("rowVersion zorunludur.");
        RuleFor(x => x.Reason).NotEmpty().WithMessage("İptal nedeni zorunludur.")
            .Must(r => r is null || r.Trim().Length is >= 3 and <= 500).WithMessage("İptal nedeni 3-500 karakter olmalıdır.");
    }
}

public sealed class InvoiceListQueryValidator : PagedRequestValidator<InvoiceListQuery>
{
    public InvoiceListQueryValidator()
    {
        ValidateSort(InvoiceService.Sorts);
        RuleFor(x => x.Type).IsInEnum().When(x => x.Type.HasValue).WithMessage("Geçersiz fatura türü.");
        RuleFor(x => x.Status).IsInEnum().When(x => x.Status.HasValue).WithMessage("Geçersiz durum.");
        RuleFor(x => x.Series).MaxLen(10);
        RuleFor(x => x.DateTo).Must((x, to) => DateRangeRules.IsValid(x.DateFrom, to))
            .WithMessage("Bitiş tarihi başlangıç tarihinden önce olamaz.");
    }
}

public sealed class PortalInvoiceListQueryValidator : PagedRequestValidator<PortalInvoiceListQuery>
{
    public PortalInvoiceListQueryValidator()
    {
        ValidateSort(InvoiceService.Sorts);
        RuleFor(x => x.Status).Must(s => s is null or InvoiceStatus.Approved or InvoiceStatus.Cancelled)
            .WithMessage("Durum yalnızca Approved veya Cancelled olabilir.");
    }
}
