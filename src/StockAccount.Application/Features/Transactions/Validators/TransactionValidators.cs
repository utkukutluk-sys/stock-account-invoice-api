using FluentValidation;
using StockAccount.Application.Common.Paging;
using StockAccount.Application.Features.Transactions.Dtos;

namespace StockAccount.Application.Features.Transactions.Validators;

public sealed class StockTransListQueryValidator : PagedRequestValidator<StockTransListQuery>
{
    public StockTransListQueryValidator()
    {
        ValidateSort(TransactionService.StockSorts);
        RuleFor(x => x.Direction).IsInEnum().When(x => x.Direction.HasValue).WithMessage("Geçersiz yön.");
        RuleFor(x => x.DateTo).Must((x, to) => DateRangeRules.IsValid(x.DateFrom, to))
            .WithMessage("Bitiş tarihi başlangıç tarihinden önce olamaz.");
    }
}

public sealed class ActTransListQueryValidator : PagedRequestValidator<ActTransListQuery>
{
    public ActTransListQueryValidator()
    {
        ValidateSort(TransactionService.ActSorts);
        RuleFor(x => x.Direction).IsInEnum().When(x => x.Direction.HasValue).WithMessage("Geçersiz yön.");
        RuleFor(x => x.DateTo).Must((x, to) => DateRangeRules.IsValid(x.DateFrom, to))
            .WithMessage("Bitiş tarihi başlangıç tarihinden önce olamaz.");
    }
}
