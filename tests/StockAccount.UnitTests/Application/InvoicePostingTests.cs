using StockAccount.Application.Common.Exceptions;
using StockAccount.Application.Features.Invoices;
using StockAccount.Domain.Common;
using StockAccount.Domain.Entities;
using StockAccount.Domain.Enums;

namespace StockAccount.UnitTests.Application;

public class InvoicePostingTests
{
    private static readonly DateTime Now = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

    private static (Invoice Invoice, Dictionary<int, Stock> Stocks, CurrentAccount Account) Scenario(
        InvoiceType type = InvoiceType.Sales,
        CurrentAccountType accountType = CurrentAccountType.Customer,
        decimal stockBalance = 100m,
        decimal quantity = 2m,
        decimal unitPrice = 50m)
    {
        var elma = new Stock { Id = 1, CompanyId = 7, Code = "ELMA", Name = "Elma", Unit = StockUnit.Kg, VatRate = 1m, Balance = stockBalance, IsActive = true };
        var account = new CurrentAccount { Id = 3, CompanyId = 7, Code = "C001", Title = "Ahmet Yılmaz", Type = accountType, IsActive = true };
        var invoice = new Invoice
        {
            Id = 10,
            CompanyId = 7,
            Type = type,
            Series = Invoice.DefaultSeriesFor(type),
            InvoiceDate = new DateOnly(2026, 10, 20),
            CurrentAccountId = account.Id,
            Lines = [new InvoiceLine { Id = 100, LineNo = 1, StockId = 1, Quantity = quantity, UnitPrice = unitPrice, VatRate = 1m }],
        };
        invoice.RecalculateTotals();
        return (invoice, new Dictionary<int, Stock> { [1] = elma }, account);
    }

    [Fact]
    public void Approve_Sales_WritesOutAndDebit_UpdatesBalances_AssignsNumber()
    {
        var (invoice, stocks, account) = Scenario();

        var result = InvoicePosting.Approve(invoice, stocks, account, number: 1, Now, userId: 5, allowNegativeStock: false);

        Assert.Equal(InvoiceStatus.Approved, invoice.Status);
        Assert.Equal(1, invoice.Number);
        Assert.Equal("SAT000001", invoice.DocumentNo);
        Assert.Equal(Now, invoice.ApprovedAt);
        Assert.Equal(5, invoice.ApprovedByUserId);

        var st = Assert.Single(result.StockTrans);
        Assert.Equal(StockTransDirection.Out, st.Direction);
        Assert.Equal(2m, st.Quantity);
        Assert.Equal(100.00m, st.Amount);
        Assert.Equal(10, st.InvoiceId);
        Assert.Equal(100, st.InvoiceLineId);
        Assert.Equal(invoice.InvoiceDate, st.TransDate);
        Assert.False(st.IsReversal);
        Assert.Equal("SAT000001 - Satış faturası", st.Description);

        Assert.NotNull(result.ActTrans);
        Assert.Equal(ActTransDirection.Debit, result.ActTrans.Direction);
        Assert.Equal(101.00m, result.ActTrans.Amount);

        Assert.Equal(98m, stocks[1].Balance);
        Assert.Equal(101.00m, account.Balance);
    }

    [Fact]
    public void Approve_Purchase_WritesInAndCredit()
    {
        var (invoice, stocks, account) = Scenario(InvoiceType.Purchase, CurrentAccountType.Supplier, stockBalance: 0m, quantity: 10m, unitPrice: 30m);

        var result = InvoicePosting.Approve(invoice, stocks, account, 4, Now, null, allowNegativeStock: false);

        Assert.Equal("ALS000004", invoice.DocumentNo);
        Assert.Equal(StockTransDirection.In, Assert.Single(result.StockTrans).Direction);
        Assert.Equal(ActTransDirection.Credit, result.ActTrans!.Direction);
        Assert.Equal(10m, stocks[1].Balance);
        Assert.Equal(-303.00m, account.Balance);
    }

    [Fact]
    public void Approve_Sales_InsufficientStock_Throws422_AndChangesNothing()
    {
        var (invoice, stocks, account) = Scenario(stockBalance: 1m);

        var ex = Assert.Throws<BusinessRuleException>(() => InvoicePosting.Approve(invoice, stocks, account, 1, Now, null, false));

        Assert.Equal(ErrorCodes.InsufficientStock, ex.Code);
        Assert.Equal(422, ex.StatusCode);
        Assert.Contains("ELMA", ex.Message);
        Assert.Equal(InvoiceStatus.Draft, invoice.Status);
        Assert.Equal(1m, stocks[1].Balance);
    }

    [Fact]
    public void Approve_InsufficientStock_SumsQuantitiesOfSameStockAcrossLines()
    {
        var (invoice, stocks, account) = Scenario(stockBalance: 3m);
        invoice.Lines.Add(new InvoiceLine { LineNo = 2, StockId = 1, Quantity = 2m, UnitPrice = 50m, VatRate = 1m });

        var ex = Assert.Throws<BusinessRuleException>(() => InvoicePosting.Approve(invoice, stocks, account, 1, Now, null, false));
        Assert.Equal(ErrorCodes.InsufficientStock, ex.Code);
    }

    [Fact]
    public void Approve_AllowNegativeStock_Succeeds()
    {
        var (invoice, stocks, account) = Scenario(stockBalance: 0m);

        InvoicePosting.Approve(invoice, stocks, account, 1, Now, null, allowNegativeStock: true);

        Assert.Equal(-2m, stocks[1].Balance);
    }

    [Theory]
    [InlineData(InvoiceType.Sales, CurrentAccountType.Supplier)]
    [InlineData(InvoiceType.Purchase, CurrentAccountType.Customer)]
    public void Approve_AccountTypeMismatch_Throws(InvoiceType type, CurrentAccountType accountType)
    {
        var (invoice, stocks, account) = Scenario(type, accountType);

        var ex = Assert.Throws<BusinessRuleException>(() => InvoicePosting.Approve(invoice, stocks, account, 1, Now, null, false));
        Assert.Equal(ErrorCodes.AccountTypeMismatch, ex.Code);
    }

    [Fact]
    public void Approve_BothTypeAccount_IsCompatibleWithSalesAndPurchase()
    {
        var (sales, s1, a1) = Scenario(InvoiceType.Sales, CurrentAccountType.Both);
        var (purchase, s2, a2) = Scenario(InvoiceType.Purchase, CurrentAccountType.Both);

        InvoicePosting.Approve(sales, s1, a1, 1, Now, null, false);
        InvoicePosting.Approve(purchase, s2, a2, 1, Now, null, false);

        Assert.Equal(InvoiceStatus.Approved, sales.Status);
        Assert.Equal(InvoiceStatus.Approved, purchase.Status);
    }

    [Fact]
    public void Approve_InactiveStockOrAccount_Throws()
    {
        var (invoice, stocks, account) = Scenario();
        stocks[1].IsActive = false;
        Assert.Equal(ErrorCodes.InactiveStock, Assert.Throws<BusinessRuleException>(() => InvoicePosting.Approve(invoice, stocks, account, 1, Now, null, false)).Code);

        stocks[1].IsActive = true;
        account.IsDeleted = true;
        Assert.Equal(ErrorCodes.InactiveAccount, Assert.Throws<BusinessRuleException>(() => InvoicePosting.Approve(invoice, stocks, account, 1, Now, null, false)).Code);
    }

    [Fact]
    public void Approve_NoLines_Throws()
    {
        var (invoice, stocks, account) = Scenario();
        invoice.Lines.Clear();

        var ex = Assert.Throws<BusinessRuleException>(() => InvoicePosting.Approve(invoice, stocks, account, 1, Now, null, false));
        Assert.Equal(ErrorCodes.InvoiceHasNoLines, ex.Code);
    }

    [Fact]
    public void Approve_NotDraft_Throws409Code()
    {
        var (invoice, stocks, account) = Scenario();
        invoice.Status = InvoiceStatus.Approved;

        var ex = Assert.Throws<DomainException>(() => InvoicePosting.Approve(invoice, stocks, account, 1, Now, null, false));
        Assert.Equal(ErrorCodes.InvoiceNotDraft, ex.Code);
        Assert.Equal(409, ErrorCodes.StatusFor(ex.Code));
    }

    [Fact]
    public void Approve_ZeroGrandTotal_WritesStockTransButNoActTrans()
    {
        var (invoice, stocks, account) = Scenario(unitPrice: 0m);

        var result = InvoicePosting.Approve(invoice, stocks, account, 1, Now, null, false);

        Assert.Single(result.StockTrans);
        Assert.Null(result.ActTrans);
        Assert.Equal(0m, account.Balance);
        Assert.Equal(98m, stocks[1].Balance);
    }

    [Fact]
    public void Cancel_ApprovedSales_WritesReversalRows_RestoresBalances()
    {
        var (invoice, stocks, account) = Scenario();
        InvoicePosting.Approve(invoice, stocks, account, 1, Now, null, false);
        var cancelTime = Now.AddDays(3);

        var result = InvoicePosting.Cancel(invoice, stocks, account, hasAccountTrans: true, "  Yanlış fatura ", cancelTime, 1, false);

        Assert.Equal(InvoiceStatus.Cancelled, invoice.Status);
        Assert.Equal("Yanlış fatura", invoice.CancelReason);
        Assert.Equal(cancelTime, invoice.CancelledAt);
        var st = Assert.Single(result.StockTrans);
        Assert.Equal(StockTransDirection.In, st.Direction);
        Assert.True(st.IsReversal);
        Assert.Equal(DateOnly.FromDateTime(cancelTime), st.TransDate);
        Assert.Equal("SAT000001 - İptal", st.Description);
        Assert.Equal(ActTransDirection.Credit, result.ActTrans!.Direction);
        Assert.True(result.ActTrans.IsReversal);
        Assert.Equal(100m, stocks[1].Balance);
        Assert.Equal(0m, account.Balance);
    }

    [Fact]
    public void Cancel_WithoutOriginalActTrans_SkipsAccountReversal()
    {
        var (invoice, stocks, account) = Scenario(unitPrice: 0m);
        InvoicePosting.Approve(invoice, stocks, account, 1, Now, null, false);

        var result = InvoicePosting.Cancel(invoice, stocks, account, hasAccountTrans: false, "sıfır tutar", Now, null, false);

        Assert.Null(result.ActTrans);
        Assert.Single(result.StockTrans);
    }

    [Fact]
    public void Cancel_Draft_ThrowsInvoiceNotApproved()
    {
        var (invoice, stocks, account) = Scenario();

        var ex = Assert.Throws<ConflictException>(() => InvoicePosting.Cancel(invoice, stocks, account, true, "neden", Now, null, false));
        Assert.Equal(ErrorCodes.InvoiceNotApproved, ex.Code);
    }

    [Fact]
    public void Cancel_Purchase_WhenGoodsAlreadySold_ThrowsInsufficientStock()
    {
        var (invoice, stocks, account) = Scenario(InvoiceType.Purchase, CurrentAccountType.Supplier, stockBalance: 0m, quantity: 10m);
        InvoicePosting.Approve(invoice, stocks, account, 1, Now, null, false);
        stocks[1].Balance = 4m; // 6 sold meanwhile

        var ex = Assert.Throws<BusinessRuleException>(() => InvoicePosting.Cancel(invoice, stocks, account, true, "neden", Now, null, false));
        Assert.Equal(ErrorCodes.InsufficientStock, ex.Code);
    }
}
