using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Domain;
using Stockia.Web.Services;
using Xunit;

namespace Stockia.Tests;

/// <summary>SR-7 (d2, written first): a purchase invoice total includes each line's VAT, and saving it raises the supplier's balance.</summary>
public class PurchaseInvoiceTests
{
    private sealed class TestClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);
    }

    private record Setup(PurchaseInvoiceService Service, StockiaDbContext Db, int A, int B, int User,
        int SupplierId, int FlourId, int TransportId);

    private static async Task<Setup> Build()
    {
        var options = new DbContextOptionsBuilder<StockiaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var db = new StockiaDbContext(options);
        var a = new Company { Name = "Company A", BaseCurrency = "USD" };
        var b = new Company { Name = "Company B", BaseCurrency = "USD" };
        db.Companies.AddRange(a, b);
        await db.SaveChangesAsync();

        var user = new AppUser { CompanyId = a.Id, Username = "kamal", PasswordHash = "x", DisplayName = "Kamal" };
        db.AppUsers.Add(user);
        foreach (var role in Enum.GetValues<PostingAccount>())
            foreach (var company in new[] { a, b })
                db.Accounts.Add(new Account { CompanyId = company.Id, Code = PostingAccountCodes.CodeOf(role), Name = role.ToString(), Type = "Asset" });

        var flour = new Item { CompanyId = a.Id, Code = "FL", Name = "Flour", Unit = "bag", Cost = 10m, SalePrice = 15m, VatRate = 11m, IsStock = true };
        var transport = new Item { CompanyId = a.Id, Code = "TR", Name = "Transport", Unit = "trip", Cost = 0m, SalePrice = 0m, VatRate = 0m, IsStock = false };
        db.Items.AddRange(flour, transport);
        db.Warehouses.Add(new Warehouse { CompanyId = a.Id, Name = "Main warehouse" });   // SR-14: stock lines need one
        await db.SaveChangesAsync();

        var rates = new ExchangeRateService(db);
        var journal = new JournalService(new EfJournalRepository(db), rates);
        var posting = new PostingService(journal, new EfPostedDocuments(db),
            new IPostingRule[] { new SalesInvoiceRule(), new PurchaseInvoiceRule() }, new AuditService(db, new TestClock()));
        var service = new PurchaseInvoiceService(db, posting, rates);

        var (supplier, error) = await service.CreateSupplierAsync(a.Id, new SupplierForm("Beirut Mills", null, null));
        Assert.Null(error);
        return new Setup(service, db, a.Id, b.Id, user.Id, supplier!.Id, flour.Id, transport.Id);
    }

    private static PurchaseInvoiceForm Form(Setup s, string number = "PI-1", params PurchaseLineForm[] lines) =>
        new(s.SupplierId, number, new DateTime(2026, 11, 24), new DateTime(2026, 12, 24), null, null,
            lines.Length > 0 ? lines : new[] { new PurchaseLineForm(s.FlourId, 10m, 20m) });

    // ---- the totals (pure) ----

    [Fact]
    public void A_purchase_invoice_total_includes_each_lines_VAT()
    {
        var invoice = new PurchaseInvoice();
        invoice.Lines.Add(new InvoiceLine { Quantity = 10m, UnitPrice = 20m, VatRate = 11m });   // 200 + 22
        invoice.Lines.Add(new InvoiceLine { Quantity = 1m, UnitPrice = 30m, VatRate = 0m });     // 30 + 0
        invoice.Recalculate();

        Assert.Equal(230m, invoice.Subtotal);
        Assert.Equal(22m, invoice.VatTotal);
        Assert.Equal(252m, invoice.Total);
    }

    [Fact]
    public void Each_line_takes_its_own_rate_and_the_total_is_the_sum_of_the_rounded_lines()
    {
        var invoice = new PurchaseInvoice();
        for (var i = 0; i < 3; i++)
            invoice.Lines.Add(new InvoiceLine { Quantity = 1m, UnitPrice = 0.05m, VatRate = 11m });   // VAT 0.0055 -> 0.01 each
        invoice.Recalculate();

        Assert.Equal(0.15m, invoice.Subtotal);
        Assert.Equal(0.03m, invoice.VatTotal);       // 3 x 0.01, not 0.0165 rounded to 0.02
        Assert.Equal(0.18m, invoice.Total);
        Assert.Equal(invoice.Lines.Sum(l => l.Total), invoice.Total);
    }

    // ---- the service ----

    [Fact]
    public async Task Saving_a_purchase_invoice_raises_the_suppliers_balance_by_its_total()
    {
        var s = await Build();
        var (invoice, errors) = await s.Service.CreateAsync(s.A, s.User, Form(s,
            lines: new[] { new PurchaseLineForm(s.FlourId, 10m, 20m), new PurchaseLineForm(s.TransportId, 1m, 30m) }));

        Assert.Empty(errors);
        Assert.Equal(252m, invoice!.Total);
        Assert.Equal(252m, (await s.Service.FindSupplierAsync(s.A, s.SupplierId))!.Balance);

        await s.Service.CreateAsync(s.A, s.User, Form(s, "PI-2", new PurchaseLineForm(s.FlourId, 1m, 100m)));   // 111
        Assert.Equal(363m, (await s.Service.FindSupplierAsync(s.A, s.SupplierId))!.Balance);
    }

    [Fact]
    public async Task The_line_keeps_the_VAT_rate_the_item_had_when_it_was_saved()
    {
        var s = await Build();
        var (invoice, _) = await s.Service.CreateAsync(s.A, s.User, Form(s));
        (await s.Db.Items.FindAsync(s.FlourId))!.VatRate = 5m;
        await s.Db.SaveChangesAsync();

        var saved = await s.Service.FindAsync(s.A, invoice!.Id);
        Assert.Equal(11m, saved!.Lines.Single().VatRate);
        Assert.Equal(222m, saved.Total);
    }

    [Fact]
    public async Task A_blank_unit_price_takes_the_item_cost()
    {
        var s = await Build();
        var (invoice, errors) = await s.Service.CreateAsync(s.A, s.User, Form(s, lines: new[] { new PurchaseLineForm(s.FlourId, 2m, null) }));
        Assert.Empty(errors);
        Assert.Equal(10m, invoice!.Lines.Single().UnitPrice);
    }

    [Fact]
    public async Task The_invoice_is_posted_to_the_journal_and_the_ledger_agrees_with_the_balance()
    {
        var s = await Build();
        var (invoice, _) = await s.Service.CreateAsync(s.A, s.User, Form(s));
        var entry = await s.Db.JournalEntries.Include(e => e.Lines)
            .SingleAsync(e => e.SourceType == DocumentTypes.PurchaseInvoice && e.SourceId == invoice!.Id);
        var payable = await s.Db.Accounts.SingleAsync(a => a.CompanyId == s.A && a.Code == PostingAccountCodes.CodeOf(PostingAccount.Payable));

        Assert.Equal(invoice!.Total, entry.Lines.Single(l => l.AccountId == payable.Id).Credit);
        Assert.Equal(invoice.Total, (await s.Service.FindSupplierAsync(s.A, s.SupplierId))!.Balance);
    }

    [Fact]
    public async Task An_invoice_that_cannot_be_posted_is_not_kept_and_the_balance_does_not_move()
    {
        var s = await Build();
        s.Db.Accounts.RemoveRange(s.Db.Accounts.Where(a => a.CompanyId == s.A && a.Code == PostingAccountCodes.CodeOf(PostingAccount.Payable)));
        await s.Db.SaveChangesAsync();

        var (invoice, errors) = await s.Service.CreateAsync(s.A, s.User, Form(s));
        Assert.Null(invoice);
        Assert.NotEmpty(errors);
        Assert.Empty(await s.Db.PurchaseInvoices.ToListAsync());
        Assert.Equal(0m, (await s.Service.FindSupplierAsync(s.A, s.SupplierId))!.Balance);
    }

    [Fact]
    public async Task Refuses_invalid_invoices()
    {
        var s = await Build();
        var ok = new PurchaseLineForm(s.FlourId, 1m, 1m);

        Assert.Contains("supplier", (await s.Service.CreateAsync(s.A, s.User, Form(s, lines: new[] { ok }) with { SupplierId = null })).Errors[0]);
        Assert.Contains("number", (await s.Service.CreateAsync(s.A, s.User, Form(s, " ", ok))).Errors[0]);
        Assert.Contains("due date", (await s.Service.CreateAsync(s.A, s.User, Form(s, "X", ok) with { DueDate = new DateTime(2026, 11, 1) })).Errors[0]);
        Assert.Contains("at least one line", (await s.Service.CreateAsync(s.A, s.User, Form(s, "X", new PurchaseLineForm(null, null, null)))).Errors[0]);
        Assert.Contains("quantity", (await s.Service.CreateAsync(s.A, s.User, Form(s, "X", new PurchaseLineForm(s.FlourId, 0m, 1m)))).Errors[0]);
        Assert.Contains("currency", (await s.Service.CreateAsync(s.A, s.User, Form(s, "X", ok) with { Currency = "12" })).Errors[0]);
        Assert.Empty(await s.Db.PurchaseInvoices.ToListAsync());
    }

    [Fact]
    public async Task Refuses_a_duplicate_number_for_the_same_supplier()
    {
        var s = await Build();
        Assert.NotNull((await s.Service.CreateAsync(s.A, s.User, Form(s))).Invoice);
        var (second, errors) = await s.Service.CreateAsync(s.A, s.User, Form(s));
        Assert.Null(second);
        Assert.Contains("already exists", errors[0]);
    }

    [Fact]
    public async Task A_foreign_currency_invoice_needs_a_rate_and_raises_the_balance_in_the_base_currency()
    {
        var s = await Build();
        var (none, errors) = await s.Service.CreateAsync(s.A, s.User, Form(s) with { Currency = "EUR" });
        Assert.Null(none);
        Assert.Contains("exchange rate", errors[0]);

        s.Db.ExchangeRates.Add(new ExchangeRate { CompanyId = s.A, Currency = "EUR", Date = new DateTime(2026, 11, 1), Rate = 1.10m });
        await s.Db.SaveChangesAsync();
        var (invoice, errors2) = await s.Service.CreateAsync(s.A, s.User, Form(s) with { Currency = "eur" });

        Assert.Empty(errors2);
        Assert.Equal(222m, invoice!.Total);                       // in EUR
        Assert.Equal(244.20m, invoice.BaseTotal);                 // in USD
        Assert.Equal(244.20m, (await s.Service.FindSupplierAsync(s.A, s.SupplierId))!.Balance);
    }

    [Fact]
    public async Task Nobody_reaches_another_companys_suppliers_or_invoices()
    {
        var s = await Build();
        var (invoice, _) = await s.Service.CreateAsync(s.A, s.User, Form(s));

        Assert.Empty(await s.Service.ListSuppliersAsync(s.B));
        Assert.Empty(await s.Service.ListAsync(s.B));
        Assert.Null(await s.Service.FindAsync(s.B, invoice!.Id));
        var (other, errors) = await s.Service.CreateAsync(s.B, s.User, Form(s));
        Assert.Null(other);
        Assert.Contains("supplier", errors[0]);
    }

    [Fact]
    public async Task Supplier_names_are_required_and_unique_in_the_company()
    {
        var s = await Build();
        Assert.NotNull((await s.Service.CreateSupplierAsync(s.A, new SupplierForm("   ", null, null))).Error);
        Assert.NotNull((await s.Service.CreateSupplierAsync(s.A, new SupplierForm("beirut mills", null, null))).Error);
        Assert.Null((await s.Service.CreateSupplierAsync(s.B, new SupplierForm("Beirut Mills", null, null))).Error);
    }
}
