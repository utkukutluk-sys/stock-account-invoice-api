using StockAccount.Domain.Common;
using StockAccount.Domain.Entities;
using StockAccount.Domain.Enums;

namespace StockAccount.UnitTests.Domain;

public class InvoiceCalculationTests
{
    [Fact]
    public void AhmetApple_TwoKgAt50_Vat1_Gives_100_1_101()
    {
        var invoice = new Invoice { Lines = [new InvoiceLine { Quantity = 2m, UnitPrice = 50m, VatRate = 1m }] };

        invoice.RecalculateTotals();

        Assert.Equal(100.00m, invoice.Lines[0].LineNet);
        Assert.Equal(1.00m, invoice.Lines[0].VatAmount);
        Assert.Equal(101.00m, invoice.Lines[0].LineTotal);
        Assert.Equal(100.00m, invoice.SubTotal);
        Assert.Equal(1.00m, invoice.VatTotal);
        Assert.Equal(101.00m, invoice.GrandTotal);
    }

    [Fact]
    public void ThreeTimes33_3333_Vat20_RoundsLineNetBeforeVat()
    {
        var line = new InvoiceLine { Quantity = 3m, UnitPrice = 33.3333m, VatRate = 20m };

        line.Calculate();

        Assert.Equal(100.00m, line.LineNet);   // 99.9999 → 100.00
        Assert.Equal(20.00m, line.VatAmount);
        Assert.Equal(120.00m, line.LineTotal);
    }

    [Theory]
    [InlineData(1, 0.125, 0, 0.13, 0.00)]    // midpoint on LineNet → away from zero
    [InlineData(1, 0.5, 1, 0.50, 0.01)]      // VAT 0.005 → 0.01
    [InlineData(1, 2.5, 10, 2.50, 0.25)]
    [InlineData(0.333, 3, 20, 1.00, 0.20)]   // 0.999 → 1.00
    public void Rounding_IsMidpointAwayFromZero(decimal qty, decimal price, decimal vat, decimal expectedNet, decimal expectedVat)
    {
        var line = new InvoiceLine { Quantity = qty, UnitPrice = price, VatRate = vat };

        line.Calculate();

        Assert.Equal(expectedNet, line.LineNet);
        Assert.Equal(expectedVat, line.VatAmount);
        Assert.Equal(expectedNet + expectedVat, line.LineTotal);
    }

    [Fact]
    public void Totals_SumRoundedLines()
    {
        var invoice = new Invoice
        {
            Lines =
            [
                new InvoiceLine { Quantity = 1m, UnitPrice = 0.125m, VatRate = 20m },
                new InvoiceLine { Quantity = 1m, UnitPrice = 0.125m, VatRate = 20m },
            ],
        };

        invoice.RecalculateTotals();

        Assert.Equal(0.26m, invoice.SubTotal);
        Assert.Equal(0.06m, invoice.VatTotal);
        Assert.Equal(0.32m, invoice.GrandTotal);
    }

    [Theory]
    [InlineData(InvoiceStatus.Approved)]
    [InlineData(InvoiceStatus.Cancelled)]
    public void EnsureDraft_Throws_ForNonDraft(InvoiceStatus status)
    {
        var invoice = new Invoice { Status = status };

        var ex = Assert.Throws<DomainException>(invoice.EnsureDraft);
        Assert.Equal("INVOICE_NOT_DRAFT", ex.Code);
    }

    [Fact]
    public void EnsureDraft_Passes_ForDraft() => new Invoice { Status = InvoiceStatus.Draft }.EnsureDraft();

    [Fact]
    public void DocumentNo_IsSeriesPlusSixDigits() => Assert.Equal("SAT000042", Invoice.FormatDocumentNo("SAT", 42));
}
