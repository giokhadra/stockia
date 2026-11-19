using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stockia.Web.Data;
using Stockia.Web.Domain;
using Stockia.Web.Services;
using Xunit;

namespace Stockia.Tests;

/// <summary>System test of item 13 (SR-4 / d3): saving a sales invoice creates a balanced entry
/// (Dr Receivable, Cr Sales, Cr VAT) linked to it, and a purchase invoice likewise.
/// The invoice pages (items 14 and 16, Sprint 4) do not exist yet, so the test saves the document through the app's
/// own PostingService, as those pages will, and then reads the result through the real pages and ledgers.
/// When items 14 and 16 are merged, replace the two PostAsync calls by a post of the invoice form.</summary>
public class PostingFlowTests : IClassFixture<StockiaFactory>
{
    private readonly StockiaFactory _factory;
    private readonly AccessSeeding.Data _data;

    public PostingFlowTests(StockiaFactory factory)
    {
        _factory = factory;
        _data = AccessSeeding.Ensure(factory, "posting");
        EnsureAccounts();
    }

    private void EnsureAccounts()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockiaDbContext>();
        foreach (var role in Enum.GetValues<PostingAccount>())
        {
            var code = PostingAccountCodes.CodeOf(role);
            if (!db.Accounts.Any(a => a.CompanyId == _data.CompanyAId && a.Code == code))
                db.Accounts.Add(new Account
                {
                    CompanyId = _data.CompanyAId, Code = code, Name = "Posting " + role, Type = "Asset"
                });
        }
        db.SaveChanges();
    }

    private int AccountId(PostingAccount role)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockiaDbContext>();
        var code = PostingAccountCodes.CodeOf(role);
        return db.Accounts.Single(a => a.CompanyId == _data.CompanyAId && a.Code == code).Id;
    }

    private async Task<HttpClient> LoginAsync()
    {
        var client = TestClient.Https(_factory);
        var response = await TestClient.LoginAsync(client, "posting-accountant", AccessSeeding.Password);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }

    private async Task<PostResult> SaveAsync(PostableDocument document)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<PostingService>()
            .PostAsync(_data.CompanyAId, _data.AccountantUserId, document);
    }

    private async Task<JournalEntry> LoadEntryAsync(int entryId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockiaDbContext>();
        return await db.JournalEntries.Include(e => e.Lines).SingleAsync(e => e.Id == entryId);
    }

    private static string NewNumber(string prefix) => prefix + "-" + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task Saving_a_sales_invoice_creates_a_balanced_entry_linked_to_it()
    {
        var number = NewNumber("SI");
        var result = await SaveAsync(new PostableDocument(DocumentTypes.SalesInvoice, 9001, number,
            new DateTime(2026, 11, 18), "Brake pads for a customer",
            new[] { new DocumentLine("Brake pads", 100m, 11m, true), new DocumentLine("Fitting", 50m, 11m, false) }));

        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        var entry = await LoadEntryAsync(result.EntryId);
        Assert.True(entry.IsBalanced);
        Assert.Equal(DocumentTypes.SalesInvoice, entry.SourceType);
        Assert.Equal(9001, entry.SourceId);
        Assert.Equal(number, entry.Reference);

        Assert.Equal(166.50m, entry.Lines.Single(l => l.AccountId == AccountId(PostingAccount.Receivable)).Debit);
        Assert.Equal(150m, entry.Lines.Single(l => l.AccountId == AccountId(PostingAccount.Sales)).Credit);
        Assert.Equal(16.50m, entry.Lines.Single(l => l.AccountId == AccountId(PostingAccount.VatCollected)).Credit);
    }

    [Fact]
    public async Task Saving_a_purchase_invoice_creates_a_balanced_entry_linked_to_it()
    {
        var number = NewNumber("PI");
        var result = await SaveAsync(new PostableDocument(DocumentTypes.PurchaseInvoice, 9002, number,
            new DateTime(2026, 11, 18), null,
            new[] { new DocumentLine("Brake pads", 200m, 11m, true), new DocumentLine("Transport", 30m, 11m, false) }));

        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        var entry = await LoadEntryAsync(result.EntryId);
        Assert.True(entry.IsBalanced);
        Assert.Equal(DocumentTypes.PurchaseInvoice, entry.SourceType);
        Assert.Equal(9002, entry.SourceId);

        Assert.Equal(200m, entry.Lines.Single(l => l.AccountId == AccountId(PostingAccount.Stock)).Debit);
        Assert.Equal(30m, entry.Lines.Single(l => l.AccountId == AccountId(PostingAccount.Purchases)).Debit);
        Assert.Equal(25.30m, entry.Lines.Single(l => l.AccountId == AccountId(PostingAccount.VatDeductible)).Debit);
        Assert.Equal(255.30m, entry.Lines.Single(l => l.AccountId == AccountId(PostingAccount.Payable)).Credit);
    }

    // BR-1: Team ARCS enters an invoice once and never retypes its entry: the entry is already in the ledgers
    [Fact]
    public async Task The_automatic_entry_is_on_its_page_and_in_the_ledger_of_each_account_with_no_retyping()
    {
        var number = NewNumber("SI");
        var result = await SaveAsync(new PostableDocument(DocumentTypes.SalesInvoice, 9003, number,
            new DateTime(2026, 11, 18), null, new[] { new DocumentLine("Oil", 80m, 11m, true) }));
        Assert.True(result.Succeeded, string.Join("; ", result.Errors));

        var client = await LoginAsync();
        var page = await client.GetStringAsync($"/Journal/Details/{result.EntryId}");
        Assert.Contains("id=\"entry-source\"", page);
        Assert.Contains(DocumentTypes.SalesInvoice, page);
        Assert.Contains(number, page);

        foreach (var role in new[] { PostingAccount.Receivable, PostingAccount.Sales, PostingAccount.VatCollected })
            Assert.Contains(number, await client.GetStringAsync($"/Ledger/Account/{AccountId(role)}"));
    }

    [Fact]
    public async Task Saving_the_same_invoice_again_does_not_post_it_twice()
    {
        var document = new PostableDocument(DocumentTypes.SalesInvoice, 9004, NewNumber("SI"),
            new DateTime(2026, 11, 18), null, new[] { new DocumentLine("Oil", 10m, 0m, true) });

        var first = await SaveAsync(document);
        var second = await SaveAsync(document);

        Assert.True(first.Succeeded, string.Join("; ", first.Errors));
        Assert.False(second.Succeeded);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockiaDbContext>();
        Assert.Equal(1, db.JournalEntries.Count(e => e.SourceType == DocumentTypes.SalesInvoice && e.SourceId == 9004));
    }
}
