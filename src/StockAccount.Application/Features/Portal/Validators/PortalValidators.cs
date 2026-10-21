using FluentValidation;
using StockAccount.Application.Common.Paging;
using StockAccount.Application.Features.Portal.Dtos;
using StockAccount.Application.Features.Transactions;

namespace StockAccount.Application.Features.Portal.Validators;

public sealed class PortalTransactionListQueryValidator : PagedRequestValidator<PortalTransactionListQuery>
{
    public PortalTransactionListQueryValidator()
    {
        ValidateSort(TransactionService.ActSorts);
        RuleFor(x => x.DateTo).Must((x, to) => DateRangeRules.IsValid(x.DateFrom, to))
            .WithMessage("Bitiş tarihi başlangıç tarihinden önce olamaz.");
    }
}
