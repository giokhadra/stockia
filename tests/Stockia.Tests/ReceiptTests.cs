using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Domain;
using Stockia.Web.Services;
using Xunit;

namespace Stockia.Tests;

/// <summary>Item 20, SR-11 (d2, written first): a receipt is allocated to the customer's open invoices; a partial
/// receipt leaves the rest due, nothing can be allocated beyond an invoice's open amount, and saving posts
/// Dr Cash or Bank, Cr Receivable (SR-4) and lowers the customer's balance.</summary>
public class ReceiptTests
{
    private record Setup(ReceiptService Receipts, SalesInvoiceService Sales, StockiaDbContext Db, int A, int B, int User,
        int CustomerId, SalesInvoice Inv1, SalesInvoice Inv2);

    private static readonly DateTime Nov20 = new(2026, 11, 20), Nov28 = new(2026, 11, 28);

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
        foreach (var company in new[] { a, b })
            db.Items.Add(new Item { CompanyId = company.Id, Code = "SV", Name = "Consulting", Unit = "h", Cost = 0m, SalePrice = 100m, VatRate = 11m, IsStock = false });
        await db.SaveChangesAsync();

        var rates = new ExchangeRateService(db);
        var posting = new PostingService(new JournalService(new EfJournalRepository(db), rates), new EfPostedDocuments(db),
            new IPostingRule[] { new SalesInvoiceRule(), new PurchaseInvoiceRule(), new ReceiptRule() }, new AuditService(db, TimeProvider.System));
        var sales = new SalesInvoiceService(db, posting, rates);
        var receipts = new ReceiptService(db, posting);

        var (customer, _) = await sales.CreateCustomerAsync(a.Id, new CustomerForm("Byblos Bakery", null, null));
        var item = db.Items.Single(i => i.CompanyId == a.Id).Id;
        var (inv1, e1) = await sales.CreateAsync(a.Id, user.Id, new SalesInvoiceForm(customer!.Id, null, Nov20, Nov20.AddDays(30), null, null,
            new[] { new SalesLineForm(item, 1m, null) }));                 // 100 + 11 VAT = 111.00
        var (inv2, e2) = await sales.CreateAsync(a.Id, user.Id, new SalesInvoiceForm(customer.Id, null, Nov20, Nov20.AddDays(30), null, null,
            new[] { new SalesLineForm(item, 0.5m, null) }));               // 50 + 5.50 VAT = 55.50
        Assert.Empty(e1.Concat(e2));
        return new Setup(receipts, sales, db, a.Id, b.Id, user.Id, customer.Id, inv1!, inv2!);
    }

    private static ReceiptForm Pay(Setup s, string method = ReceiptMethods.Bank, params (int Invoice, decimal? Amount)[] parts) =>
        new(s.CustomerId, null, Nov28, method, null, parts.Select(p => new ReceiptAllocationForm(p.Invoice, p.Amount)).ToList());

    private static async Task<decimal> Balance(Setup s) => (await s.Sales.FindCustomerAsync(s.A, s.CustomerId))!.Balance;

    // ---- the acceptance criterion ----

    [Fact]
    public async Task A_receipt_covering_two_invoices_marks_both_as_paid_and_brings_the_balance_to_zero()
    {
        var s = await Build();
        Assert.Equal(166.50m, await Balance(s));

        var (receipt, errors) = await s.Receipts.CreateAsync(s.A, s.User,
            Pay(s, ReceiptMethods.Bank, (s.Inv1.Id, 111.00m), (s.Inv2.Id, 55.50m)));

        Assert.Empty(errors);
        Assert.Equal(166.50m, receipt!.Amount);
        Assert.Equal(2, receipt.Allocations.Count);
        var open = await s.Receipts.OpenInvoicesAsync(s.A, s.CustomerId);
        Assert.Empty(open);                                            // both paid
        Assert.Equal(0.00m, await Balance(s));
    }

    [Fact]
    public async Task A_partial_receipt_leaves_the_rest_of_the_invoice_due()
    {
        var s = await Build();
        var (_, errors) = await s.Receipts.CreateAsync(s.A, s.User, Pay(s, ReceiptMethods.Cash, (s.Inv1.Id, 50m)));

        Assert.Empty(errors);
        var open = await s.Receipts.OpenInvoicesAsync(s.A, s.CustomerId);
        Assert.Equal(61.00m, open.Single(o => o.Invoice.Id == s.Inv1.Id).Open);
        Assert.Equal(55.50m, open.Single(o => o.Invoice.Id == s.Inv2.Id).Open);
        Assert.Equal(116.50m, await Balance(s));
    }

    // ---- the posting (SR-4) ----

    [Theory]
    [InlineData(ReceiptMethods.Cash, PostingAccount.Cash)]
    [InlineData(ReceiptMethods.Bank, PostingAccount.Bank)]
    public async Task Saving_posts_cash_or_bank_against_the_receivable(string method, PostingAccount debited)
    {
        var s = await Build();
        var (receipt, errors) = await s.Receipts.CreateAsync(s.A, s.User, Pay(s, method, (s.Inv1.Id, 111m), (s.Inv2.Id, 20m)));
        Assert.Empty(errors);

        var entry = await s.Db.JournalEntries.Include(e => e.Lines)
            .SingleAsync(e => e.SourceType == DocumentTypes.Receipt && e.SourceId == receipt!.Id);
        int Id(PostingAccount p) => s.Db.Accounts.Single(a => a.CompanyId == s.A && a.Code == PostingAccountCodes.CodeOf(p)).Id;
        Assert.Equal(131.00m, entry.Lines.Single(l => l.AccountId == Id(debited)).Debit);
        Assert.Equal(131.00m, entry.Lines.Single(l => l.AccountId == Id(PostingAccount.Receivable)).Credit);
        Assert.Equal(receipt!.Number, entry.Reference);
    }

    [Fact]
    public void The_receipt_rule_debits_cash_or_bank_and_credits_the_receivable()
    {
        var doc = new PostableDocument(DocumentTypes.Receipt, 1, "R-0001", Nov28, null,
            new[] { new DocumentLine("S-0001", 70m, 0m, false), new DocumentLine("S-0002", 30.55m, 0m, false) }, PostingAccount.Cash);
        var lines = new ReceiptRule().Build(doc);

        Assert.Equal(new[] { new PostingLine(PostingAccount.Cash, 100.55m, 0m), new PostingLine(PostingAccount.Receivable, 0m, 100.55m) }, lines);
    }

    // ---- refusals ----

    [Fact]
    public async Task Nothing_can_be_allocated_beyond_an_invoices_open_amount()
    {
        var s = await Build();
        Assert.Empty((await s.Receipts.CreateAsync(s.A, s.User, Pay(s, ReceiptMethods.Cash, (s.Inv1.Id, 100m)))).Errors);

        var (receipt, errors) = await s.Receipts.CreateAsync(s.A, s.User, Pay(s, ReceiptMethods.Cash, (s.Inv1.Id, 11.01m)));

        Assert.Null(receipt);
        Assert.Contains(errors, e => e.Contains(s.Inv1.Number) && e.Contains("11.00"));   // says what is still open
        Assert.Equal(66.50m, await Balance(s));                                           // the refused one changed nothing
        Assert.Single(await s.Receipts.ListAsync(s.A));
    }

    [Fact]
    public async Task An_empty_or_negative_allocation_is_refused()
    {
        var s = await Build();
        var (r1, e1) = await s.Receipts.CreateAsync(s.A, s.User, Pay(s, ReceiptMethods.Cash, (s.Inv1.Id, null), (s.Inv2.Id, 0m)));
        var (r2, e2) = await s.Receipts.CreateAsync(s.A, s.User, Pay(s, ReceiptMethods.Cash, (s.Inv1.Id, -5m)));

        Assert.Null(r1);
        Assert.Contains(e1, e => e.Contains("at least one invoice"));
        Assert.Null(r2);
        Assert.Contains(e2, e => e.Contains("cannot be negative"));
    }

    [Fact]
    public async Task An_invoice_of_another_customer_or_company_is_refused()
    {
        var s = await Build();
        var (other, _) = await s.Sales.CreateCustomerAsync(s.A, new CustomerForm("Tyre Sweets", null, null));

        var (r1, e1) = await s.Receipts.CreateAsync(s.A, s.User, Pay(s, ReceiptMethods.Cash, (s.Inv1.Id, 10m)) with { CustomerId = other!.Id });
        var (r2, e2) = await s.Receipts.CreateAsync(s.B, s.User, Pay(s, ReceiptMethods.Cash, (s.Inv1.Id, 10m)));

        Assert.Null(r1);
        Assert.Contains(e1, e => e.Contains("not an open invoice of this customer"));
        Assert.Null(r2);
        Assert.Contains(e2, e => e.Contains("Choose a customer"));
    }

    [Fact]
    public async Task An_invoice_waiting_for_approval_cannot_be_paid()
    {
        var s = await Build();
        s.Inv2.Status = InvoiceStatuses.PendingApproval;
        await s.Db.SaveChangesAsync();

        var (receipt, errors) = await s.Receipts.CreateAsync(s.A, s.User, Pay(s, ReceiptMethods.Cash, (s.Inv2.Id, 10m)));

        Assert.Null(receipt);
        Assert.Contains(errors, e => e.Contains("not an open invoice"));
        Assert.DoesNotContain(await s.Receipts.OpenInvoicesAsync(s.A, s.CustomerId), o => o.Invoice.Id == s.Inv2.Id);
    }

    [Fact]
    public async Task The_method_must_be_cash_or_bank()
    {
        var s = await Build();
        var (receipt, errors) = await s.Receipts.CreateAsync(s.A, s.User, Pay(s, "Cheque", (s.Inv1.Id, 10m)));
        Assert.Null(receipt);
        Assert.Contains(errors, e => e.Contains("Cash or Bank"));
    }

    // ---- currency and numbering ----

    [Fact]
    public async Task A_foreign_invoice_is_paid_in_its_currency_and_settles_its_exact_base_amount()
    {
        var s = await Build();
        Assert.Empty(await new ExchangeRateService(s.Db).SetAsync(s.A, "EUR", new DateTime(2026, 11, 1), 1.1m));
        var item = s.Db.Items.Single(i => i.CompanyId == s.A).Id;
        var (eur, _) = await s.Sales.CreateAsync(s.A, s.User, new SalesInvoiceForm(s.CustomerId, null, Nov20, Nov20.AddDays(30), "EUR", null,
            new[] { new SalesLineForm(item, 1m, 33.33m) }));               // EUR 33.33 + 3.67 VAT = 37.00; base 36.66 + 4.03 = 40.69
        Assert.Equal(40.69m, eur!.BaseTotal);

        var (part, e1) = await s.Receipts.CreateAsync(s.A, s.User, Pay(s, ReceiptMethods.Bank, (eur.Id, 12.34m)));
        var (mixed, e3) = await s.Receipts.CreateAsync(s.A, s.User, Pay(s, ReceiptMethods.Bank, (s.Inv1.Id, 10m), (eur.Id, 1m)));
        var (rest, e2) = await s.Receipts.CreateAsync(s.A, s.User, Pay(s, ReceiptMethods.Bank, (eur.Id, 24.66m)));

        Assert.Empty(e1.Concat(e2));
        Assert.Equal("EUR", part!.Currency);
        Assert.Equal(40.69m, part.BaseAmount + rest!.BaseAmount);      // fully paid: exactly the invoice's base total
        Assert.Null(mixed);
        Assert.Contains(e3, e => e.Contains("same currency"));
        Assert.Equal(166.50m, await Balance(s));                        // 166.50 + 40.69 - 40.69
    }

    [Fact]
    public async Task A_blank_number_takes_the_next_receipt_number()
    {
        var s = await Build();
        var (r1, _) = await s.Receipts.CreateAsync(s.A, s.User, Pay(s, ReceiptMethods.Cash, (s.Inv1.Id, 1m)));
        var (r2, _) = await s.Receipts.CreateAsync(s.A, s.User, Pay(s, ReceiptMethods.Cash, (s.Inv1.Id, 1m)));
        Assert.Equal(("R-0001", "R-0002"), (r1!.Number, r2!.Number));
    }
}
