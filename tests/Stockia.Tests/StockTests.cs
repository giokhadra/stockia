using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Domain;
using Stockia.Web.Services;
using Xunit;

namespace Stockia.Tests;

/// <summary>SR-14 (d2, written first): saving a purchase invoice of stock items raises the stock of the chosen
/// warehouse, by the quantity and by the value; a service never moves stock; nothing moves when the invoice fails.</summary>
public class StockTests
{
    private sealed class TestClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);
    }

    private record Setup(PurchaseInvoiceService Purchases, StockService Stock, StockiaDbContext Db,
        int A, int B, int User, int SupplierId, int FlourId, int TransportId, int[] Warehouses, int WarehouseOfB);

    private static async Task<Setup> Build(int warehouses = 1)
    {
        var options = new DbContextOptionsBuilder<StockiaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var db = new StockiaDbContext(options);
        var a = new Company { Name = "Company A", BaseCurrency = "USD" };
        var b = new Company { Name = "Company B", BaseCurrency = "USD" };
        db.Companies.AddRange(a, b);
        await db.SaveChangesAsync();

        var user = new AppUser { CompanyId = a.Id, Username = "joy", PasswordHash = "x", DisplayName = "Joy" };
        db.AppUsers.Add(user);
        foreach (var role in Enum.GetValues<PostingAccount>())
            db.Accounts.Add(new Account { CompanyId = a.Id, Code = PostingAccountCodes.CodeOf(role), Name = role.ToString(), Type = "Asset" });

        var flour = new Item { CompanyId = a.Id, Code = "FL", Name = "Flour", Unit = "bag", Cost = 10m, SalePrice = 15m, VatRate = 11m, IsStock = true };
        var transport = new Item { CompanyId = a.Id, Code = "TR", Name = "Transport", Unit = "trip", VatRate = 0m, IsStock = false };
        db.Items.AddRange(flour, transport);
        var ours = Enumerable.Range(1, warehouses).Select(n => new Warehouse { CompanyId = a.Id, Name = "Warehouse " + n }).ToList();
        var theirs = new Warehouse { CompanyId = b.Id, Name = "B warehouse" };
        db.Warehouses.AddRange(ours);
        db.Warehouses.Add(theirs);
        await db.SaveChangesAsync();

        var rates = new ExchangeRateService(db);
        var journal = new JournalService(new EfJournalRepository(db), rates);
        var posting = new PostingService(journal, new EfPostedDocuments(db),
            new IPostingRule[] { new SalesInvoiceRule(), new PurchaseInvoiceRule() }, new AuditService(db, new TestClock()));
        var purchases = new PurchaseInvoiceService(db, posting, rates);
        var (supplier, error) = await purchases.CreateSupplierAsync(a.Id, new SupplierForm("Beirut Mills", null, null));
        Assert.Null(error);

        return new Setup(purchases, new StockService(db), db, a.Id, b.Id, user.Id, supplier!.Id, flour.Id, transport.Id,
            ours.Select(w => w.Id).ToArray(), theirs.Id);
    }

    private static PurchaseInvoiceForm Form(Setup s, string number, int? warehouseId, params PurchaseLineForm[] lines) =>
        new(s.SupplierId, number, new DateTime(2026, 11, 24), new DateTime(2026, 12, 24), null, null, lines, warehouseId);

    // BRD SR-14 acceptance: "Recording a purchase of 10 units raises stock by 10 immediately"
    [Fact]
    public async Task A_purchase_of_10_units_raises_stock_by_10_immediately()
    {
        var s = await Build();
        Assert.Equal(0m, await s.Stock.QuantityAsync(s.A, s.FlourId));

        var (invoice, errors) = await s.Purchases.CreateAsync(s.A, s.User,
            Form(s, "PI-1", s.Warehouses[0], new PurchaseLineForm(s.FlourId, 10m, 20m)));

        Assert.Empty(errors);
        Assert.Equal(10m, await s.Stock.QuantityAsync(s.A, s.FlourId, s.Warehouses[0]));
        Assert.Equal(200m, await s.Stock.ValueAsync(s.A, s.FlourId, s.Warehouses[0]));    // value before VAT
        Assert.Equal(s.Warehouses[0], invoice!.WarehouseId);
    }

    [Fact]
    public async Task A_second_purchase_adds_to_the_first()
    {
        var s = await Build();
        await s.Purchases.CreateAsync(s.A, s.User, Form(s, "PI-1", null, new PurchaseLineForm(s.FlourId, 10m, 20m)));
        await s.Purchases.CreateAsync(s.A, s.User, Form(s, "PI-2", null, new PurchaseLineForm(s.FlourId, 5m, 26m)));

        Assert.Equal(15m, await s.Stock.QuantityAsync(s.A, s.FlourId));
        Assert.Equal(330m, await s.Stock.ValueAsync(s.A, s.FlourId));                     // 200 + 130
    }

    [Fact]
    public async Task Each_movement_keeps_its_invoice_line_its_date_and_type_In()
    {
        var s = await Build();
        var (invoice, _) = await s.Purchases.CreateAsync(s.A, s.User, Form(s, "PI-1", null, new PurchaseLineForm(s.FlourId, 3m, 7m)));

        var movement = await s.Db.StockMovements.SingleAsync();
        Assert.Equal(invoice!.Lines.Single().Id, movement.InvoiceLineId);
        Assert.Equal(new DateTime(2026, 11, 24), movement.Date);
        Assert.Equal(StockMovementTypes.In, movement.Type);
        Assert.Equal(s.A, movement.CompanyId);
    }

    [Fact]
    public async Task A_service_line_never_moves_stock()
    {
        var s = await Build();
        await s.Purchases.CreateAsync(s.A, s.User, Form(s, "PI-1", null,
            new PurchaseLineForm(s.FlourId, 10m, 20m), new PurchaseLineForm(s.TransportId, 1m, 30m)));

        Assert.Equal(0m, await s.Stock.QuantityAsync(s.A, s.TransportId));
        Assert.Single(await s.Db.StockMovements.ToListAsync());
    }

    [Fact]
    public async Task An_invoice_of_services_only_needs_no_warehouse()
    {
        var s = await Build(warehouses: 2);
        var (invoice, errors) = await s.Purchases.CreateAsync(s.A, s.User, Form(s, "PI-1", null, new PurchaseLineForm(s.TransportId, 1m, 30m)));

        Assert.Empty(errors);
        Assert.Null(invoice!.WarehouseId);
        Assert.Empty(await s.Db.StockMovements.ToListAsync());
    }

    [Fact]
    public async Task The_stock_goes_to_the_chosen_warehouse_only()
    {
        var s = await Build(warehouses: 2);
        await s.Purchases.CreateAsync(s.A, s.User, Form(s, "PI-1", s.Warehouses[1], new PurchaseLineForm(s.FlourId, 10m, 20m)));

        Assert.Equal(0m, await s.Stock.QuantityAsync(s.A, s.FlourId, s.Warehouses[0]));
        Assert.Equal(10m, await s.Stock.QuantityAsync(s.A, s.FlourId, s.Warehouses[1]));
    }

    [Fact]
    public async Task With_two_warehouses_and_none_chosen_the_invoice_is_refused_and_nothing_moves()
    {
        var s = await Build(warehouses: 2);
        var (invoice, errors) = await s.Purchases.CreateAsync(s.A, s.User, Form(s, "PI-1", null, new PurchaseLineForm(s.FlourId, 10m, 20m)));

        Assert.Null(invoice);
        Assert.Contains("warehouse", errors[0]);
        Assert.Empty(await s.Db.PurchaseInvoices.ToListAsync());
        Assert.Empty(await s.Db.StockMovements.ToListAsync());
    }

    [Fact]
    public async Task Without_any_warehouse_a_stock_purchase_is_refused()
    {
        var s = await Build(warehouses: 0);
        var (invoice, errors) = await s.Purchases.CreateAsync(s.A, s.User, Form(s, "PI-1", null, new PurchaseLineForm(s.FlourId, 10m, 20m)));

        Assert.Null(invoice);
        Assert.Contains("Add a warehouse first", errors[0]);
    }

    // SR-33: a warehouse of another company is never used
    [Fact]
    public async Task A_warehouse_of_another_company_is_refused()
    {
        var s = await Build();
        var (invoice, errors) = await s.Purchases.CreateAsync(s.A, s.User, Form(s, "PI-1", s.WarehouseOfB, new PurchaseLineForm(s.FlourId, 10m, 20m)));

        Assert.Null(invoice);
        Assert.Contains("warehouse of this company", errors[0]);
        Assert.Empty(await s.Db.StockMovements.ToListAsync());
    }

    [Fact]
    public async Task Nothing_moves_when_the_invoice_cannot_be_posted()
    {
        var s = await Build();
        s.Db.Accounts.RemoveRange(s.Db.Accounts);           // no accounts: the entry cannot be posted (SR-4)
        await s.Db.SaveChangesAsync();

        var (invoice, errors) = await s.Purchases.CreateAsync(s.A, s.User, Form(s, "PI-1", null, new PurchaseLineForm(s.FlourId, 10m, 20m)));

        Assert.Null(invoice);
        Assert.NotEmpty(errors);
        Assert.Empty(await s.Db.StockMovements.ToListAsync());
    }

    // UC-3: stock and ledger agree
    [Fact]
    public async Task The_stock_value_equals_the_stock_debit_of_the_purchase_entry()
    {
        var s = await Build();
        await s.Purchases.CreateAsync(s.A, s.User, Form(s, "PI-1", null,
            new PurchaseLineForm(s.FlourId, 3m, 0.35m), new PurchaseLineForm(s.TransportId, 1m, 30m)));

        var stockAccount = await s.Db.Accounts.SingleAsync(a => a.CompanyId == s.A && a.Code == PostingAccountCodes.CodeOf(PostingAccount.Stock));
        var debited = await s.Db.JournalLines.Where(l => l.AccountId == stockAccount.Id).SumAsync(l => l.Debit);
        Assert.Equal(1.05m, debited);
        Assert.Equal(debited, await s.Stock.ValueAsync(s.A, s.FlourId));
    }

    // SR-19: the value of stock bought in another currency is kept in the base currency
    [Fact]
    public async Task Stock_bought_in_another_currency_is_valued_in_the_base_currency()
    {
        var s = await Build();
        s.Db.ExchangeRates.Add(new ExchangeRate { CompanyId = s.A, Currency = "EUR", Date = new DateTime(2026, 11, 1), Rate = 1.10m });
        await s.Db.SaveChangesAsync();

        var (_, errors) = await s.Purchases.CreateAsync(s.A, s.User,
            Form(s, "PI-1", null, new PurchaseLineForm(s.FlourId, 10m, 20m)) with { Currency = "EUR" });

        Assert.Empty(errors);
        Assert.Equal(10m, await s.Stock.QuantityAsync(s.A, s.FlourId));
        Assert.Equal(220m, await s.Stock.ValueAsync(s.A, s.FlourId));                     // 200 EUR x 1.10
    }

    [Fact]
    public async Task Another_company_sees_none_of_the_stock()
    {
        var s = await Build();
        await s.Purchases.CreateAsync(s.A, s.User, Form(s, "PI-1", null, new PurchaseLineForm(s.FlourId, 10m, 20m)));

        Assert.Equal(0m, await s.Stock.QuantityAsync(s.B, s.FlourId));
        Assert.Empty(await s.Stock.QuantitiesAsync(s.B));
        Assert.Equal(10m, (await s.Stock.QuantitiesAsync(s.A))[s.FlourId]);
    }
}