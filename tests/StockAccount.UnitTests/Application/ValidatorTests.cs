using StockAccount.Application.Common.Paging;
using StockAccount.Application.Features.CurrentAccounts.Dtos;
using StockAccount.Application.Features.CurrentAccounts.Validators;
using StockAccount.Application.Features.Invoices.Dtos;
using StockAccount.Application.Features.Invoices.Validators;
using StockAccount.Application.Features.Stocks;
using StockAccount.Application.Features.Stocks.Dtos;
using StockAccount.Application.Features.Stocks.Validators;
using StockAccount.Application.Features.Users.Dtos;
using StockAccount.Application.Features.Users.Validators;
using StockAccount.Domain.Entities;
using StockAccount.Domain.Enums;

namespace StockAccount.UnitTests.Application;

public class ValidatorTests
{
    private static StockSaveRequest ValidStock() => new() { Code = "elma-1", Name = "Elma", Unit = StockUnit.Kg, VatRate = 1m, UnitPrice = 50m };

    private static InvoiceSaveRequest ValidInvoice() => new()
    {
        Type = InvoiceType.Sales,
        InvoiceDate = new DateOnly(2026, 10, 21),
        CurrentAccountId = 1,
        Lines = [new InvoiceLineRequest { StockId = 1, Quantity = 2m, UnitPrice = 50m, VatRate = 1m }],
    };

    [Fact]
    public void Stock_Valid_LowercaseCodeIsNormalized()
    {
        Assert.True(new StockSaveRequestValidator().Validate(ValidStock()).IsValid);
    }

    [Theory]
    [InlineData("EL MA")]
    [InlineData("ELMA!")]
    [InlineData("")]
    public void Stock_InvalidCode_Fails(string code)
    {
        var request = ValidStock();
        request.Code = code;

        var result = new StockSaveRequestValidator().Validate(request);

        Assert.Contains(result.Errors, e => e.PropertyName == nameof(StockSaveRequest.Code));
    }

    [Theory]
    [InlineData(7)]
    [InlineData(18)]
    public void Stock_VatRateOutsideSet_Fails(decimal vat)
    {
        var request = ValidStock();
        request.VatRate = vat;

        Assert.Contains(new StockSaveRequestValidator().Validate(request).Errors, e => e.PropertyName == nameof(StockSaveRequest.VatRate));
    }

    [Fact]
    public void Stock_NegativePriceOrTooManyDecimals_Fails()
    {
        var request = ValidStock();
        request.UnitPrice = -1m;
        Assert.False(new StockSaveRequestValidator().Validate(request).IsValid);

        request.UnitPrice = 1.23456m;
        Assert.False(new StockSaveRequestValidator().Validate(request).IsValid);
    }

    [Theory]
    [InlineData("123456789", false)]
    [InlineData("1234567890", true)]
    [InlineData("12345678901", true)]
    [InlineData("12345A7890", false)]
    [InlineData(null, true)]
    public void Account_TaxNumber_Is10To11DigitsWhenPresent(string? taxNumber, bool valid)
    {
        var request = new CurrentAccountSaveRequest { Code = "C001", Title = "Ahmet", Type = CurrentAccountType.Customer, TaxNumber = taxNumber };

        Assert.Equal(valid, new CurrentAccountSaveRequestValidator().Validate(request).IsValid);
    }

    [Fact]
    public void Invoice_Valid() => Assert.True(new InvoiceSaveRequestValidator().Validate(ValidInvoice()).IsValid);

    [Fact]
    public void Invoice_NoLines_Fails()
    {
        var request = ValidInvoice();
        request.Lines.Clear();

        Assert.Contains(new InvoiceSaveRequestValidator().Validate(request).Errors, e => e.PropertyName == "Lines");
    }

    [Fact]
    public void Invoice_MoreThan200Lines_Fails()
    {
        var request = ValidInvoice();
        request.Lines = Enumerable.Range(0, 201).Select(_ => new InvoiceLineRequest { StockId = 1, Quantity = 1m, VatRate = 0m }).ToList();

        Assert.Contains(new InvoiceSaveRequestValidator().Validate(request).Errors, e => e.PropertyName == "Lines");
    }

    [Theory]
    [InlineData(0, 50, 1, "Lines[0].Quantity")]
    [InlineData(1.2345, 50, 1, "Lines[0].Quantity")]
    [InlineData(1, -1, 1, "Lines[0].UnitPrice")]
    [InlineData(1, 50, 5, "Lines[0].VatRate")]
    public void Invoice_LineRules(decimal qty, decimal price, decimal vat, string property)
    {
        var request = ValidInvoice();
        request.Lines[0] = new InvoiceLineRequest { StockId = 1, Quantity = qty, UnitPrice = price, VatRate = vat };

        Assert.Contains(new InvoiceSaveRequestValidator().Validate(request).Errors, e => e.PropertyName == property);
    }

    [Fact]
    public void Invoice_DueDateBeforeInvoiceDate_Fails()
    {
        var request = ValidInvoice();
        request.DueDate = request.InvoiceDate!.Value.AddDays(-1);

        Assert.Contains(new InvoiceSaveRequestValidator().Validate(request).Errors, e => e.PropertyName == "DueDate");
    }

    [Fact]
    public void Invoice_MissingDate_Fails()
    {
        var request = ValidInvoice();
        request.InvoiceDate = null;

        Assert.Contains(new InvoiceSaveRequestValidator().Validate(request).Errors, e => e.PropertyName == "InvoiceDate");
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("ab", false)]
    [InlineData("Yanlış", true)]
    public void Cancel_ReasonIs3To500(string? reason, bool valid) =>
        Assert.Equal(valid, new InvoiceCancelRequestValidator().Validate(new InvoiceCancelRequest { RowVersion = "AAAAAAAAB9E=", Reason = reason }).IsValid);

    [Fact]
    public void User_RoleUser_RequiresCompany()
    {
        var request = new UserCreateRequest { Email = "a@b.com", FullName = "A", Password = "Abcdef12", Role = "User" };

        Assert.Contains(new UserCreateRequestValidator().Validate(request).Errors, e => e.PropertyName == "CompanyId");
    }

    [Fact]
    public void User_RoleAdmin_MustNotHaveCompanyOrAccount()
    {
        var request = new UserCreateRequest { Email = "a@b.com", FullName = "A", Password = "Abcdef12", Role = "Admin", CompanyId = 1, CurrentAccountId = 2 };

        var errors = new UserCreateRequestValidator().Validate(request).Errors;
        Assert.Contains(errors, e => e.PropertyName == "CompanyId");
        Assert.Contains(errors, e => e.PropertyName == "CurrentAccountId");
    }

    [Theory]
    [InlineData("abcdefg1", false)]  // no upper
    [InlineData("ABCDEFG1", false)]  // no lower
    [InlineData("Abcdefgh", false)]  // no digit
    [InlineData("Abc12", false)]     // too short
    [InlineData("Abcdef12", true)]
    public void User_PasswordPolicy(string password, bool valid)
    {
        var request = new UserCreateRequest { Email = "a@b.com", FullName = "A", Password = password, Role = "User", CompanyId = 1 };

        Assert.Equal(valid, new UserCreateRequestValidator().Validate(request).IsValid);
    }

    [Fact]
    public void User_UnknownRole_Fails()
    {
        var request = new UserCreateRequest { Email = "a@b.com", FullName = "A", Password = "Abcdef12", Role = "Root" };

        Assert.Contains(new UserCreateRequestValidator().Validate(request).Errors, e => e.PropertyName == "Role");
    }

    [Theory]
    [InlineData(0, 20, "Page")]
    [InlineData(1, 0, "PageSize")]
    [InlineData(1, 101, "PageSize")]
    public void Paging_Bounds(int page, int pageSize, string property)
    {
        var errors = new StockListQueryValidator().Validate(new StockListQuery { Page = page, PageSize = pageSize }).Errors;

        Assert.Contains(errors, e => e.PropertyName == property);
    }

    [Theory]
    [InlineData("-code,name", true)]
    [InlineData("balance", true)]
    [InlineData("foo", false)]
    [InlineData("code,-password", false)]
    public void Paging_SortWhitelist(string sort, bool valid) =>
        Assert.Equal(valid, new StockListQueryValidator().Validate(new StockListQuery { Sort = sort }).IsValid);

    [Fact]
    public void ApplySort_UnknownField_Throws()
    {
        var query = new List<Stock>().AsQueryable();

        var ex = Assert.Throws<InvalidSortFieldException>(() => query.ApplySort("hacked", StockService.Sorts));
        Assert.Equal("hacked", ex.Field);
    }

    [Fact]
    public void ApplySort_OrdersAndBreaksTiesById()
    {
        var stocks = new List<Stock>
        {
            new() { Id = 3, Code = "B" },
            new() { Id = 1, Code = "B" },
            new() { Id = 2, Code = "A" },
        }.AsQueryable();

        var ids = stocks.ApplySort("-code", StockService.Sorts).Select(s => s.Id).ToList();

        Assert.Equal([1, 3, 2], ids);
    }
}
