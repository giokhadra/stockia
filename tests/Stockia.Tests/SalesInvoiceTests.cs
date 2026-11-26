using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Domain;
using Stockia.Web.Services;
using Xunit;

namespace Stockia.Tests;

/// <summary>Item 16, SR-10 (d2, written first): a sales invoice accepts stock and service lines, its total includes
/// VAT, and saving it raises the customer's balance and posts its entry (SR-4).</summary>
public class SalesInvoiceTests
{
    private sealed class TestClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 26, 9, 0, 0, TimeSpan.Zero);
    }

    private record Setup(SalesInvoiceService Service, StockiaDbContext Db, int A, int B, int User,
        int CustomerId, int FlourId, int TransportId, int OtherCompanyItemId);

    private static async Task<Setup> Build()
    {
        var db = new StockiaDbContext(new DbContextOptionsBuilder<StockiaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var a = new Company { Name = "Company A", BaseCurrency = "USD" };
        var b = new Company { Name = "Company B", BaseCurrency = "USD" };
        db.Companies.AddRange(a, b);
        await db.SaveChangesAsync();

        var user = new AppUser { CompanyId = a.Id, Username = "gio", PasswordHash = "x", DisplayName = "Gio" };
        db.AppUsers.Add(user);
        foreach (var role in Enum.GetValues<PostingAccount>())
            foreach (var company in new[] { a, b })
                db.Accounts.Add(new Account { CompanyId = company.Id, Code = PostingAccountCodes.CodeOf(role), Name = role.ToString(), Type = "Asset" });

        var flour = new Item { CompanyId = a.Id, Code = "FL", Name = "Flour", Unit = "bag", Cost = 10m, SalePrice = 15m, VatRate = 11m, IsStock = true };
        var transport = new Item { CompanyId = a.Id, Code = "TR", Name = "Delivery", Unit = "trip", Cost = 0m, SalePrice = 20m, VatRate = 0m, IsStock = false };
        var foreign = new Item { CompanyId = b.Id, Code = "FL", Name = "Flour B", Unit = "bag", Cost = 10m, SalePrice = 15m, VatRate = 11m, IsStock = true };
        db.Items.AddRange(flour, transport, foreign);
        db.Warehouses.Add(new Warehouse { CompanyId = a.Id, Name = "Main warehouse" });
        await db.SaveChangesAsync();

        var rates = new ExchangeRateService(db);
        var journal = new JournalService(new EfJournalRepository(db), rates);
        var posting = new PostingService(journal, new EfPostedDocuments(db),
            new IPostingRule[] { new SalesInvoiceRule(), new PurchaseInvoiceRule() }, new AuditService(db, new TestClock()));
        var service = new SalesInvoiceService(db, posting, rates);

        var (customer, error) = await service.CreateCustomerAsync(a.Id, new CustomerForm("Byblos Bakery", null, null));
        Assert.Null(error);
        return new Setup(service, db, a.Id, b.Id, user.Id, customer!.Id, flour.Id, transport.Id, foreign.Id);
    }

    private static readonly DateTime Nov26 = new(2026, 11, 26);

    private static SalesInvoiceForm Form(Setup s, string? number = "S-100", params SalesLineForm[] lines) =>
        new(s.CustomerId, number, Nov26, Nov26.AddDays(30), null, null,
            lines.Length > 0 ? lines : new[] { new SalesLineForm(s.FlourId, 10m, null) });

    // ---- stock and service lines, VAT ----

    [Fact]
    public async Task A_sales_invoice_accepts_stock_and_service_lines_and_its_total_includes_VAT()
    {
        var s = await Build();
        var (invoice, errors) = await s.Service.CreateAsync(s.A, s.User,
            Form(s, "S-100", new SalesLineForm(s.FlourId, 10m, 15m), new SalesLineForm(s.TransportId, 1m, 20m)));

        Assert.Empty(errors);
        Assert.Equal(new[] { true, false }, invoice!.Lines.Select(l => l.IsStock));
        Assert.Equal(170.00m, invoice.Subtotal);       // 150.00 + 20.00
        Assert.Equal(16.50m, invoice.VatTotal);        // 11% of 150.00; the service has no VAT
        Assert.Equal(186.50m, invoice.Total);
        Assert.Equal(186.50m, invoice.BaseTotal);
    }

    [Fact]
    public async Task A_blank_unit_price_takes_the_items_sale_price_and_the_items_VAT_rate_is_kept_on_the_line()
    {
        var s = await Build();
        var (invoice, errors) = await s.Service.CreateAsync(s.A, s.User, Form(s));

        Assert.Empty(errors);
        var line = Assert.Single(invoice!.Lines);
        Assert.Equal(15m, line.UnitPrice);
        Assert.Equal(11m, line.VatRate);
    }

    [Fact]
    public async Task A_service_only_invoice_needs_no_warehouse()
    {
        var s = await Build();
        s.Db.Warehouses.RemoveRange(s.Db.Warehouses);
        await s.Db.SaveChangesAsync();

        var (invoice, errors) = await s.Service.CreateAsync(s.A, s.User, Form(s, "S-101", new SalesLineForm(s.TransportId, 2m, null)));

        Assert.Empty(errors);
        Assert.Null(invoice!.WarehouseId);
    }

    // ---- the customer's balance and the posting ----

    [Fact]
    public async Task Saving_raises_the_customers_balance_and_posts_the_receivable()
    {
        var s = await Build();
        var (invoice, errors) = await s.Service.CreateAsync(s.A, s.User,
            Form(s, "S-102", new SalesLineForm(s.FlourId, 10m, 15m), new SalesLineForm(s.TransportId, 1m, 20m)));
        Assert.Empty(errors);

        var customer = await s.Service.FindCustomerAsync(s.A, s.CustomerId);
        Assert.Equal(186.50m, customer!.Balance);

        var entry = await s.Db.JournalEntries.Include(e => e.Lines)
            .SingleAsync(e => e.SourceType == DocumentTypes.SalesInvoice && e.SourceId == invoice!.Id);
        var receivable = await s.Db.Accounts.SingleAsync(a => a.CompanyId == s.A && a.Code == PostingAccountCodes.CodeOf(PostingAccount.Receivable));
        Assert.Equal(186.50m, entry.Lines.Where(l => l.AccountId == receivable.Id).Sum(l => l.Debit));
        Assert.Equal(entry.TotalDebit, entry.TotalCredit);
    }

    [Fact]
    public async Task An_invoice_in_another_currency_raises_the_balance_in_the_base_currency()
    {
        var s = await Build();
        Assert.Empty(await new ExchangeRateService(s.Db).SetAsync(s.A, "EUR", new DateTime(2026, 11, 1), 1.10m));

        var (invoice, errors) = await s.Service.CreateAsync(s.A, s.User,
            Form(s, "S-103", new SalesLineForm(s.TransportId, 5m, 20m)) with { Currency = "eur" });

        Assert.Empty(errors);
        Assert.Equal("EUR", invoice!.Currency);
        Assert.Equal(100.00m, invoice.Total);
        Assert.Equal(110.00m, invoice.BaseTotal);
        Assert.Equal(110.00m, (await s.Service.FindCustomerAsync(s.A, s.CustomerId))!.Balance);
    }

    // ---- numbering ----

    [Fact]
    public async Task A_blank_number_takes_the_next_number_of_the_company()
    {
        var s = await Build();
        var (first, e1) = await s.Service.CreateAsync(s.A, s.User, Form(s, null));
        var (second, e2) = await s.Service.CreateAsync(s.A, s.User, Form(s, "  "));

        Assert.Empty(e1);
        Assert.Empty(e2);
        Assert.Equal("S-0001", first!.Number);
        Assert.Equal("S-0002", second!.Number);
    }

    [Fact]
    public async Task A_number_is_used_once_in_the_company_even_for_another_customer()
    {
        var s = await Build();
        var (other, _) = await s.Service.CreateCustomerAsync(s.A, new CustomerForm("Tyre Sweets", null, null));
        Assert.Empty((await s.Service.CreateAsync(s.A, s.User, Form(s, "S-200"))).Errors);

        var (dup, errors) = await s.Service.CreateAsync(s.A, s.User, Form(s, "S-200") with { CustomerId = other!.Id });

        Assert.Null(dup);
        Assert.Contains(errors, e => e.Contains("S-200 already exists"));
        Assert.Equal(15m * 10 * 1.11m, (await s.Service.FindCustomerAsync(s.A, s.CustomerId))!.Balance);   // only the first counted
    }

    // ---- refusals and company isolation (SR-33) ----

    [Fact]
    public async Task A_customer_or_item_of_another_company_is_refused()
    {
        var s = await Build();
        var (bCustomer, _) = await s.Service.CreateCustomerAsync(s.B, new CustomerForm("B customer", null, null));

        var (i1, e1) = await s.Service.CreateAsync(s.A, s.User, Form(s) with { CustomerId = bCustomer!.Id });
        var (i2, e2) = await s.Service.CreateAsync(s.A, s.User, Form(s, "S-301", new SalesLineForm(s.OtherCompanyItemId, 1m, null)));

        Assert.Null(i1);
        Assert.Contains(e1, e => e.Contains("Choose a customer"));
        Assert.Null(i2);
        Assert.Contains(e2, e => e.Contains("choose an item of this company"));
        Assert.Empty(await s.Service.ListAsync(s.A));
        Assert.DoesNotContain(await s.Service.ListCustomersAsync(s.A), c => c.Name == "B customer");
    }

    [Fact]
    public async Task Wrong_lines_and_dates_are_refused_with_their_reason()
    {
        var s = await Build();
        var (invoice, errors) = await s.Service.CreateAsync(s.A, s.User,
            new SalesInvoiceForm(s.CustomerId, "S-400", Nov26, Nov26.AddDays(-1), null, null,
                new[] { new SalesLineForm(s.FlourId, 0m, null), new SalesLineForm(s.TransportId, 1m, -5m) }));

        Assert.Null(invoice);
        Assert.Contains(errors, e => e.Contains("due date cannot be before"));
        Assert.Contains(errors, e => e.Contains("Line 1") && e.Contains("quantity"));
        Assert.Contains(errors, e => e.Contains("Line 2") && e.Contains("negative"));
    }

    [Fact]
    public async Task A_customer_name_is_required_and_unique_in_the_company()
    {
        var s = await Build();
        Assert.Equal("The customer name is required.", (await s.Service.CreateCustomerAsync(s.A, new CustomerForm(" ", null, null))).Error);
        Assert.Contains("already exists", (await s.Service.CreateCustomerAsync(s.A, new CustomerForm("byblos bakery", null, null))).Error);
        Assert.Null((await s.Service.CreateCustomerAsync(s.B, new CustomerForm("Byblos Bakery", null, null))).Error);  // other company
    }
}
