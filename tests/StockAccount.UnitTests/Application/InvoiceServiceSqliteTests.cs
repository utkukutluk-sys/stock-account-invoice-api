using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using StockAccount.Application.Common.Abstractions;
using StockAccount.Application.Common.Caching;
using StockAccount.Application.Common.Exceptions;
using StockAccount.Application.Features.Invoices;
using StockAccount.Application.Features.Invoices.Dtos;
using StockAccount.Domain.Entities;
using StockAccount.Domain.Enums;
using StockAccount.Infrastructure.Persistence;

namespace StockAccount.UnitTests.Application;

/// <summary>InvoiceService end-to-end on SQLite in-memory (transactions, numbering, ledger rows, balances).</summary>
public sealed class InvoiceServiceSqliteTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly TestDbContext _db;
    private readonly ICacheInvalidator _invalidator = Substitute.For<ICacheInvalidator>();
    private readonly ICurrentUser _user = Substitute.For<ICurrentUser>();
    private readonly InvoiceService _service;
    private readonly Stock _elma;
    private readonly CurrentAccount _ahmet;
    private readonly CurrentAccount _supplier;

    public InvoiceServiceSqliteTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        // CHECK constraints of the SQL Server model use LEN(); provide it for SQLite.
        _connection.CreateFunction("LEN", (string? s) => s?.Length ?? 0);

        _db = new TestDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        var company = new Company { Name = "Demo", TaxNumber = "1234567890" };
        _db.Companies.Add(company);
        _db.SaveChanges();
        _elma = new Stock { CompanyId = company.Id, Code = "ELMA", Name = "Elma", Unit = StockUnit.Kg, VatRate = 1m, UnitPrice = 50m, Balance = 100m };
        _ahmet = new CurrentAccount { CompanyId = company.Id, Code = "C001", Title = "Ahmet Yılmaz", Type = CurrentAccountType.Customer };
        _supplier = new CurrentAccount { CompanyId = company.Id, Code = "S001", Title = "Bereket", Type = CurrentAccountType.Supplier };
        _db.AddRange(_elma, _ahmet, _supplier);
        _db.SaveChanges();
        _db.ChangeTracker.Clear();

        _user.CompanyId.Returns(company.Id);
        _user.UserId.Returns(2);
        _service = new InvoiceService(
            _db, _user, Substitute.For<IDatabaseErrorInspector>(), _invalidator,
            Options.Create(new InventoryOptions()), TimeProvider.System, NullLogger<InvoiceService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private Task<InvoiceDto> CreateSalesAsync(decimal quantity, int? accountId = null, InvoiceType type = InvoiceType.Sales) =>
        _service.CreateAsync(new InvoiceSaveRequest
        {
            Type = type,
            InvoiceDate = new DateOnly(2026, 10, 21),
            CurrentAccountId = accountId ?? _ahmet.Id,
            Lines = [new InvoiceLineRequest { StockId = _elma.Id, Quantity = quantity, UnitPrice = 50m, VatRate = 1m }],
        });

    [Fact]
    public async Task Create_IsDraft_WithServerTotals()
    {
        var draft = await CreateSalesAsync(2m);

        Assert.Equal(InvoiceStatus.Draft, draft.Status);
        Assert.Equal("SAT", draft.Series);
        Assert.Null(draft.Number);
        Assert.Equal(101.00m, draft.GrandTotal);
        Assert.Equal("ELMA", Assert.Single(draft.Lines).StockCode);
    }

    [Fact]
    public async Task Approve_AhmetApple_WritesLedgers_UpdatesBalances_InvalidatesCache()
    {
        var draft = await CreateSalesAsync(2m);

        var approved = await _service.ApproveAsync(draft.Id, new InvoiceApproveRequest { RowVersion = draft.RowVersion });

        Assert.Equal(InvoiceStatus.Approved, approved.Status);
        Assert.Equal("SAT000001", approved.DocumentNo);
        var st = Assert.Single(await _db.StockTrans.AsNoTracking().ToListAsync());
        Assert.Equal((StockTransDirection.Out, 2m, draft.Id), (st.Direction, st.Quantity, st.InvoiceId));
        var at = Assert.Single(await _db.ActTrans.AsNoTracking().ToListAsync());
        Assert.Equal((ActTransDirection.Debit, 101.00m), (at.Direction, at.Amount));
        Assert.Equal(98m, (await _db.Stocks.AsNoTracking().SingleAsync(s => s.Id == _elma.Id)).Balance);
        Assert.Equal(101.00m, (await _db.CurrentAccounts.AsNoTracking().SingleAsync(a => a.Id == _ahmet.Id)).Balance);
        await _invalidator.Received(1).InvoicePostedAsync(Arg.Any<int>(), Arg.Is<IEnumerable<int>>(ids => ids.Contains(_elma.Id)), _ahmet.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Approve_NumbersSequentiallyPerSeries()
    {
        var a = await CreateSalesAsync(1m);
        var b = await CreateSalesAsync(1m);

        var first = await _service.ApproveAsync(a.Id, new InvoiceApproveRequest { RowVersion = a.RowVersion });
        var second = await _service.ApproveAsync(b.Id, new InvoiceApproveRequest { RowVersion = b.RowVersion });

        Assert.Equal(1, first.Number);
        Assert.Equal(2, second.Number);
        Assert.Equal("SAT000002", second.DocumentNo);
    }

    [Fact]
    public async Task Approve_InsufficientStock_Throws_AndLeavesDraft()
    {
        var draft = await CreateSalesAsync(500m);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => _service.ApproveAsync(draft.Id, new InvoiceApproveRequest { RowVersion = draft.RowVersion }));

        Assert.Equal(ErrorCodes.InsufficientStock, ex.Code);
        Assert.Equal(InvoiceStatus.Draft, (await _service.GetAsync(draft.Id)).Status);
        Assert.Empty(await _db.StockTrans.ToListAsync());
    }

    [Fact]
    public async Task Create_PurchaseWithCustomerAccount_ThrowsAccountTypeMismatch()
    {
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => CreateSalesAsync(1m, _ahmet.Id, InvoiceType.Purchase));
        Assert.Equal(ErrorCodes.AccountTypeMismatch, ex.Code);
    }

    [Fact]
    public async Task Approve_StaleRowVersion_ThrowsConcurrencyConflict()
    {
        var draft = await CreateSalesAsync(1m);

        var ex = await Assert.ThrowsAsync<ConflictException>(() => _service.ApproveAsync(draft.Id, new InvoiceApproveRequest { RowVersion = "AAAAAAAAAAI=" }));
        Assert.Equal(ErrorCodes.ConcurrencyConflict, ex.Code);
    }

    [Fact]
    public async Task Cancel_WritesReversalRows_AndRestoresBalances()
    {
        var draft = await CreateSalesAsync(2m);
        var approved = await _service.ApproveAsync(draft.Id, new InvoiceApproveRequest { RowVersion = draft.RowVersion });

        var cancelled = await _service.CancelAsync(approved.Id, new InvoiceCancelRequest { RowVersion = approved.RowVersion, Reason = "Hatalı giriş" });

        Assert.Equal(InvoiceStatus.Cancelled, cancelled.Status);
        Assert.Equal("Hatalı giriş", cancelled.CancelReason);
        var stockRows = await _db.StockTrans.AsNoTracking().OrderBy(t => t.Id).ToListAsync();
        Assert.Equal([StockTransDirection.Out, StockTransDirection.In], stockRows.Select(r => r.Direction));
        Assert.True(stockRows[1].IsReversal);
        var actRows = await _db.ActTrans.AsNoTracking().OrderBy(t => t.Id).ToListAsync();
        Assert.Equal([ActTransDirection.Debit, ActTransDirection.Credit], actRows.Select(r => r.Direction));
        Assert.Equal(100m, (await _db.Stocks.AsNoTracking().SingleAsync(s => s.Id == _elma.Id)).Balance);
        Assert.Equal(0m, (await _db.CurrentAccounts.AsNoTracking().SingleAsync(a => a.Id == _ahmet.Id)).Balance);
    }

    [Fact]
    public async Task UpdateAndDelete_OnlyForDraft()
    {
        var draft = await CreateSalesAsync(1m);
        var approved = await _service.ApproveAsync(draft.Id, new InvoiceApproveRequest { RowVersion = draft.RowVersion });

        var update = new InvoiceSaveRequest
        {
            InvoiceDate = new DateOnly(2026, 10, 21),
            CurrentAccountId = _ahmet.Id,
            RowVersion = approved.RowVersion,
            Lines = [new InvoiceLineRequest { StockId = _elma.Id, Quantity = 1m, UnitPrice = 1m, VatRate = 0m }],
        };
        Assert.Equal("INVOICE_NOT_DRAFT", (await Assert.ThrowsAsync<StockAccount.Domain.Common.DomainException>(() => _service.UpdateAsync(approved.Id, update))).Code);
        Assert.Equal("INVOICE_NOT_DRAFT", (await Assert.ThrowsAsync<StockAccount.Domain.Common.DomainException>(() => _service.DeleteAsync(approved.Id))).Code);
    }

    [Fact]
    public async Task Update_ReplacesLines_AndRecomputesTotals()
    {
        var draft = await CreateSalesAsync(1m);

        var updated = await _service.UpdateAsync(draft.Id, new InvoiceSaveRequest
        {
            InvoiceDate = new DateOnly(2026, 10, 22),
            CurrentAccountId = _ahmet.Id,
            RowVersion = draft.RowVersion,
            Lines =
            [
                new InvoiceLineRequest { StockId = _elma.Id, Quantity = 3m, UnitPrice = 10m, VatRate = 20m },
                new InvoiceLineRequest { StockId = _elma.Id, Quantity = 1m, UnitPrice = 5m, VatRate = 0m },
            ],
        });

        Assert.Equal(2, updated.Lines.Count);
        Assert.Equal([1, 2], updated.Lines.Select(l => l.LineNo));
        Assert.Equal(35m, updated.SubTotal);
        Assert.Equal(6m, updated.VatTotal);
        Assert.Equal(41m, updated.GrandTotal);
    }

    /// <summary>SQLite cannot generate SQL Server rowversion values: give them a static default so inserts succeed.</summary>
    private sealed class TestDbContext(DbContextOptions<AppDbContext> options) : AppDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            foreach (var entity in builder.Model.GetEntityTypes())
            {
                // SQL Server CHECK constraints compare decimals numerically; SQLite stores decimals as TEXT.
                foreach (var check in entity.GetCheckConstraints().ToList())
                {
                    entity.RemoveCheckConstraint(check.ModelName);
                }

                var rowVersion = entity.FindProperty("RowVersion");
                if (rowVersion is not null)
                {
                    builder.Entity(entity.ClrType).Property("RowVersion")
                        .ValueGeneratedNever()
                        .HasDefaultValue(new byte[] { 0, 0, 0, 0, 0, 0, 0, 1 });
                }
            }
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            foreach (var entry in ChangeTracker.Entries<StockAccount.Domain.Common.IHasRowVersion>())
            {
                if (entry.State == EntityState.Added && entry.Entity.RowVersion.Length == 0)
                {
                    entry.Entity.RowVersion = [0, 0, 0, 0, 0, 0, 0, 1];
                }
            }

            return base.SaveChangesAsync(cancellationToken);
        }

        public override int SaveChanges()
        {
            foreach (var entry in ChangeTracker.Entries<StockAccount.Domain.Common.IHasRowVersion>())
            {
                if (entry.State == EntityState.Added && entry.Entity.RowVersion.Length == 0)
                {
                    entry.Entity.RowVersion = [0, 0, 0, 0, 0, 0, 0, 1];
                }
            }

            return base.SaveChanges();
        }
    }
}
