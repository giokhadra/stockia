using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Domain;
using Stockia.Web.Security;
using Stockia.Web.Services;
using Xunit;

namespace Stockia.Tests;

/// <summary>Item 17, SR-15 / UC-5 / UC-9 (d2, written first): a sale lowers the stock of its warehouse at the weighted
/// average cost; a sale beyond stock is kept but not posted until a chief accountant or an administrator approves
/// it (then the stock falls) or rejects it (then nothing moves).</summary>
public class StockSaleTests
{
    private sealed class TestClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);
    }

    private record Setup(SalesInvoiceService Sales, StockService Stock, StockiaDbContext Db, int A, int B,
        int Accountant, int Chief, int Admin, int CustomerId, int FlourId, int TransportId, int WarehouseId);

    private static readonly DateTime Nov26 = new(2026, 11, 26);

    /// <summary>Company A holds 10 bags of flour worth 100.00 (10.00 each) in its only warehouse.</summary>
    private static async Task<Setup> Build()
    {
        var db = new StockiaDbContext(new DbContextOptionsBuilder<StockiaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var a = new Company { Name = "Company A", BaseCurrency = "USD" };
        var b = new Company { Name = "Company B", BaseCurrency = "USD" };
        db.Companies.AddRange(a, b);
        foreach (var name in CompanyRoles.Readers) db.Roles.Add(new Role { Name = name });
        await db.SaveChangesAsync();

        int User(string username, string role)
        {
            var user = new AppUser { CompanyId = a.Id, Username = username, PasswordHash = "x", DisplayName = username };
            db.AppUsers.Add(user);
            db.SaveChanges();
            db.Assignments.Add(new Assignment { UserId = user.Id, CompanyId = a.Id, RoleId = db.Roles.Single(r => r.Name == role).Id });
            db.SaveChanges();
            return user.Id;
        }
        var accountant = User("joy", CompanyRoles.Accountant);
        var chief = User("chief", CompanyRoles.ChiefAccountant);
        var admin = User("admin", CompanyRoles.Administrator);

        foreach (var role in Enum.GetValues<PostingAccount>())
            db.Accounts.Add(new Account { CompanyId = a.Id, Code = PostingAccountCodes.CodeOf(role), Name = role.ToString(), Type = "Asset" });
        var flour = new Item { CompanyId = a.Id, Code = "FL", Name = "Flour", Unit = "bag", SalePrice = 15m, VatRate = 11m, IsStock = true };
        var transport = new Item { CompanyId = a.Id, Code = "TR", Name = "Delivery", Unit = "trip", SalePrice = 20m, VatRate = 0m, IsStock = false };
        var main = new Warehouse { CompanyId = a.Id, Name = "Main warehouse" };
        db.AddRange(flour, transport, main);
        await db.SaveChangesAsync();
        db.StockMovements.Add(In(a.Id, flour.Id, main.Id, 10m, 100m));
        await db.SaveChangesAsync();

        var rates = new ExchangeRateService(db);
        var posting = new PostingService(new JournalService(new EfJournalRepository(db), rates), new EfPostedDocuments(db),
            new IPostingRule[] { new SalesInvoiceRule(), new PurchaseInvoiceRule() }, new AuditService(db, new TestClock()));
        var sales = new SalesInvoiceService(db, posting, rates);
        var (customer, error) = await sales.CreateCustomerAsync(a.Id, new CustomerForm("Byblos Bakery", null, null));
        Assert.Null(error);
        return new Setup(sales, new StockService(db), db, a.Id, b.Id, accountant, chief, admin, customer!.Id, flour.Id, transport.Id, main.Id);
    }

    private static StockMovement In(int company, int item, int warehouse, decimal quantity, decimal value) => new()
    {
        CompanyId = company, ItemId = item, WarehouseId = warehouse, Date = new DateTime(2026, 11, 1),
        Type = StockMovementTypes.In, Quantity = quantity, Value = value
    };

    private static SalesInvoiceForm Sale(Setup s, params SalesLineForm[] lines) =>
        new(s.CustomerId, null, Nov26, Nov26.AddDays(30), null, null, lines);

    private async Task<bool> IsPostedAsync(Setup s, int invoiceId) =>
        await s.Db.JournalEntries.AnyAsync(e => e.SourceType == DocumentTypes.SalesInvoice && e.SourceId == invoiceId);

    private static async Task<decimal> BalanceAsync(Setup s) => (await s.Sales.FindCustomerAsync(s.A, s.CustomerId))!.Balance;

    // ---- within stock: the stock falls at once ----

    [Fact]
    public async Task A_sale_within_stock_lowers_it_at_once_at_the_average_cost()
    {
        var s = await Build();
        var (invoice, errors) = await s.Sales.CreateAsync(s.A, s.Accountant, Sale(s, new SalesLineForm(s.FlourId, 4m, null)));

        Assert.Empty(errors);
        Assert.Equal(InvoiceStatuses.Posted, invoice!.Status);
        Assert.Equal(6m, await s.Stock.QuantityAsync(s.A, s.FlourId, s.WarehouseId));
        Assert.Equal(60m, await s.Stock.ValueAsync(s.A, s.FlourId, s.WarehouseId));

        var movement = await s.Db.StockMovements.SingleAsync(m => m.Type == StockMovementTypes.Out);
        Assert.Equal(-4m, movement.Quantity);
        Assert.Equal(-40m, movement.Value);
        Assert.Equal(invoice.Lines.Single().Id, movement.InvoiceLineId);
        Assert.Equal(Nov26, movement.Date);
    }

    [Fact]
    public async Task Selling_all_of_it_leaves_no_quantity_and_no_value()
    {
        var s = await Build();
        s.Db.StockMovements.Add(In(s.A, s.FlourId, s.WarehouseId, 2m, 0.01m));   // 12 bags worth 100.01: no round average
        await s.Db.SaveChangesAsync();

        await s.Sales.CreateAsync(s.A, s.Accountant, Sale(s, new SalesLineForm(s.FlourId, 5m, null), new SalesLineForm(s.FlourId, 7m, null)));

        Assert.Equal(0m, await s.Stock.QuantityAsync(s.A, s.FlourId));
        Assert.Equal(0m, await s.Stock.ValueAsync(s.A, s.FlourId));
    }

    [Fact]
    public async Task The_value_leaves_at_the_weighted_average_of_the_purchases()
    {
        var s = await Build();
        s.Db.StockMovements.Add(In(s.A, s.FlourId, s.WarehouseId, 10m, 130m));    // 20 bags worth 230: 11.50 each
        await s.Db.SaveChangesAsync();

        await s.Sales.CreateAsync(s.A, s.Accountant, Sale(s, new SalesLineForm(s.FlourId, 5m, null)));

        Assert.Equal(15m, await s.Stock.QuantityAsync(s.A, s.FlourId));
        Assert.Equal(172.50m, await s.Stock.ValueAsync(s.A, s.FlourId));            // 230 - 5 x 11.50
    }

    [Fact]
    public async Task A_service_line_never_moves_stock()
    {
        var s = await Build();
        await s.Sales.CreateAsync(s.A, s.Accountant, Sale(s, new SalesLineForm(s.FlourId, 1m, null), new SalesLineForm(s.TransportId, 3m, null)));

        Assert.Single(await s.Db.StockMovements.Where(m => m.Type == StockMovementTypes.Out).ToListAsync());
        Assert.Equal(0m, await s.Stock.QuantityAsync(s.A, s.TransportId));
    }

    // ---- beyond stock: it waits (BRD SR-15 acceptance: "Selling 12 units with 10 in stock shows a warning and
    //      needs confirmation by an authorized role") ----

    [Fact]
    public async Task Selling_12_with_10_in_stock_is_kept_but_waits_for_approval_and_nothing_moves()
    {
        var s = await Build();
        var (invoice, errors) = await s.Sales.CreateAsync(s.A, s.Accountant, Sale(s, new SalesLineForm(s.FlourId, 12m, null)));

        Assert.Empty(errors);
        Assert.Equal(InvoiceStatuses.PendingApproval, invoice!.Status);
        Assert.False(await IsPostedAsync(s, invoice.Id));
        Assert.Equal(0m, await BalanceAsync(s));
        Assert.Equal(10m, await s.Stock.QuantityAsync(s.A, s.FlourId));
        Assert.Single(await s.Sales.ListAwaitingApprovalAsync(s.A));

        var shortage = Assert.Single(await s.Sales.ShortagesOfAsync(s.A, invoice.Id));
        Assert.Equal(12m, shortage.Wanted);
        Assert.Equal(10m, shortage.Available);

        var log = await s.Db.AuditLogs.SingleAsync(l => l.Action == AuditActions.SaleAwaitingApproval);
        Assert.Contains(invoice.Number, log.Details);
        Assert.Equal(s.Accountant, log.UserId);
    }

    [Fact]
    public async Task Two_lines_of_the_same_item_count_together()
    {
        var s = await Build();
        var (invoice, _) = await s.Sales.CreateAsync(s.A, s.Accountant,
            Sale(s, new SalesLineForm(s.FlourId, 6m, null), new SalesLineForm(s.FlourId, 6m, null)));

        Assert.Equal(InvoiceStatuses.PendingApproval, invoice!.Status);
    }

    [Fact]
    public async Task A_chief_accountant_approves_then_the_sale_is_posted_and_the_stock_falls()
    {
        var s = await Build();
        var (pending, _) = await s.Sales.CreateAsync(s.A, s.Accountant, Sale(s, new SalesLineForm(s.FlourId, 12m, 15m)));

        var (approved, errors) = await s.Sales.ApproveAsync(s.A, s.Chief, pending!.Id);

        Assert.Empty(errors);
        Assert.Equal(InvoiceStatuses.Posted, approved!.Status);
        Assert.Equal(s.Chief, approved.DecidedByUserId);
        Assert.NotNull(approved.DecidedAt);
        Assert.True(await IsPostedAsync(s, pending.Id));
        Assert.Equal(199.80m, await BalanceAsync(s));                               // 12 x 15 + 11%
        Assert.Equal(-2m, await s.Stock.QuantityAsync(s.A, s.FlourId));               // below zero, as approved
        Assert.Equal(0m, await s.Stock.ValueAsync(s.A, s.FlourId));
        Assert.Empty(await s.Sales.ListAwaitingApprovalAsync(s.A));
        Assert.Single(await s.Db.AuditLogs.Where(l => l.Action == AuditActions.SaleApproved && l.UserId == s.Chief).ToListAsync());
    }

    [Fact]
    public async Task An_administrator_may_approve_too()
    {
        var s = await Build();
        var (pending, _) = await s.Sales.CreateAsync(s.A, s.Accountant, Sale(s, new SalesLineForm(s.FlourId, 12m, null)));

        var (approved, errors) = await s.Sales.ApproveAsync(s.A, s.Admin, pending!.Id);

        Assert.Empty(errors);
        Assert.Equal(InvoiceStatuses.Posted, approved!.Status);
    }

    [Fact]
    public async Task An_accountant_cannot_approve_or_reject_and_nothing_changes()
    {
        var s = await Build();
        var (pending, _) = await s.Sales.CreateAsync(s.A, s.Accountant, Sale(s, new SalesLineForm(s.FlourId, 12m, null)));

        var (approved, e1) = await s.Sales.ApproveAsync(s.A, s.Accountant, pending!.Id);
        var (rejected, e2) = await s.Sales.RejectAsync(s.A, s.Accountant, pending.Id);

        Assert.Null(approved);
        Assert.Null(rejected);
        Assert.Contains("chief accountant or an administrator", e1[0]);
        Assert.Contains("chief accountant or an administrator", e2[0]);
        Assert.Equal(InvoiceStatuses.PendingApproval, (await s.Sales.FindAsync(s.A, pending.Id))!.Status);
        Assert.False(await IsPostedAsync(s, pending.Id));
        Assert.Equal(10m, await s.Stock.QuantityAsync(s.A, s.FlourId));
    }

    [Fact]
    public async Task Rejecting_keeps_the_invoice_as_a_record_and_moves_nothing()
    {
        var s = await Build();
        var (pending, _) = await s.Sales.CreateAsync(s.A, s.Accountant, Sale(s, new SalesLineForm(s.FlourId, 12m, null)));

        var (rejected, errors) = await s.Sales.RejectAsync(s.A, s.Chief, pending!.Id);

        Assert.Empty(errors);
        Assert.Equal(InvoiceStatuses.Rejected, rejected!.Status);
        Assert.Equal(s.Chief, rejected.DecidedByUserId);
        Assert.False(await IsPostedAsync(s, pending.Id));
        Assert.Equal(0m, await BalanceAsync(s));
        Assert.Equal(10m, await s.Stock.QuantityAsync(s.A, s.FlourId));
        Assert.Single(await s.Db.AuditLogs.Where(l => l.Action == AuditActions.SaleRejected && l.Details == pending.Number).ToListAsync());
    }

    [Fact]
    public async Task A_sale_is_decided_only_once()
    {
        var s = await Build();
        var (first, _) = await s.Sales.CreateAsync(s.A, s.Accountant, Sale(s, new SalesLineForm(s.FlourId, 12m, null)));
        var (second, _) = await s.Sales.CreateAsync(s.A, s.Accountant, Sale(s, new SalesLineForm(s.FlourId, 11m, null)));
        await s.Sales.ApproveAsync(s.A, s.Chief, first!.Id);
        await s.Sales.RejectAsync(s.A, s.Chief, second!.Id);

        Assert.Contains("not waiting for approval", (await s.Sales.ApproveAsync(s.A, s.Chief, first.Id)).Errors[0]);
        Assert.Contains("not waiting for approval", (await s.Sales.RejectAsync(s.A, s.Chief, first.Id)).Errors[0]);
        Assert.Contains("not waiting for approval", (await s.Sales.ApproveAsync(s.A, s.Chief, second.Id)).Errors[0]);
        Assert.Single(await s.Db.JournalEntries.Where(e => e.SourceType == DocumentTypes.SalesInvoice).ToListAsync());
    }

    [Fact]
    public async Task A_sale_cannot_be_approved_from_another_company()
    {
        var s = await Build();
        var (pending, _) = await s.Sales.CreateAsync(s.A, s.Accountant, Sale(s, new SalesLineForm(s.FlourId, 12m, null)));

        var (approved, errors) = await s.Sales.ApproveAsync(s.B, s.Chief, pending!.Id);

        Assert.Null(approved);
        Assert.Contains("not found", errors[0]);
        Assert.Empty(await s.Sales.ListAwaitingApprovalAsync(s.B));
    }

    [Fact]
    public async Task An_approval_that_cannot_be_posted_leaves_the_sale_waiting()
    {
        var s = await Build();
        var (pending, _) = await s.Sales.CreateAsync(s.A, s.Accountant, Sale(s, new SalesLineForm(s.FlourId, 12m, null)));
        s.Db.Accounts.RemoveRange(s.Db.Accounts);                                    // SR-4: the entry cannot be posted
        await s.Db.SaveChangesAsync();

        var (approved, errors) = await s.Sales.ApproveAsync(s.A, s.Chief, pending!.Id);

        Assert.Null(approved);
        Assert.NotEmpty(errors);
        Assert.Equal(InvoiceStatuses.PendingApproval, (await s.Sales.FindAsync(s.A, pending.Id))!.Status);
        Assert.Equal(10m, await s.Stock.QuantityAsync(s.A, s.FlourId));
        Assert.Equal(0m, await BalanceAsync(s));
    }
}