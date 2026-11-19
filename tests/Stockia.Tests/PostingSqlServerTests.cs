using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Domain;
using Stockia.Web.Services;
using Xunit;

namespace Stockia.Tests;

/// <summary>Runs only when STOCKIA_TEST_CONNECTION is set (GitHub Actions, after db/009_posting_v1.sql).
/// Proves the EF model and the real JournalEntry columns agree, and that the table itself lets a document be
/// posted only once and keeps SourceType and SourceId together, even when the service is bypassed.</summary>
[Collection("SqlServer")]
public class PostingSqlServerTests
{
    private static DbContextOptions<StockiaDbContext> Options() =>
        new DbContextOptionsBuilder<StockiaDbContext>()
            .UseSqlServer(Environment.GetEnvironmentVariable("STOCKIA_TEST_CONNECTION")).Options;

    [SqlServerFact]
    public async Task A_sales_invoice_is_posted_with_its_link_and_the_table_allows_it_only_once()
    {
        using var db = new StockiaDbContext(Options());

        var (company, error) = await new CompanyService(db)
            .CreateAsync("SQL posting " + Guid.NewGuid().ToString("N")[..8], null, "USD");
        Assert.Null(error);

        var user = new AppUser
        {
            CompanyId = company!.Id, Username = "sql-posting-" + Guid.NewGuid().ToString("N")[..8],
            PasswordHash = "x", DisplayName = "SQL posting"
        };
        db.AppUsers.Add(user);
        foreach (var role in Enum.GetValues<PostingAccount>())
            db.Accounts.Add(new Account
            {
                CompanyId = company.Id, Code = PostingAccountCodes.CodeOf(role), Name = role.ToString(), Type = "Asset"
            });
        await db.SaveChangesAsync();

        var posting = new PostingService(new JournalService(new EfJournalRepository(db)), new EfPostedDocuments(db),
            new IPostingRule[] { new SalesInvoiceRule(), new PurchaseInvoiceRule() },
            new AuditService(db, TimeProvider.System));
        var number = "SQL-" + Guid.NewGuid().ToString("N")[..8];
        var result = await posting.PostAsync(company.Id, user.Id, new PostableDocument(
            DocumentTypes.SalesInvoice, 501, number, DateTime.Today, "SQL Server test",
            new[] { new DocumentLine("Part", 100.10m, 11m, true) }));
        Assert.True(result.Succeeded, string.Join("; ", result.Errors));

        var entry = await db.JournalEntries.Include(e => e.Lines).SingleAsync(e => e.Id == result.EntryId);
        Assert.Equal(DocumentTypes.SalesInvoice, entry.SourceType);
        Assert.Equal(501, entry.SourceId);
        Assert.True(entry.IsBalanced);
        Assert.Equal(111.11m, entry.TotalDebit);   // 100.10 + 11.01 VAT

        // UX_JournalEntry_Source in the table, not only the check in the service
        var receivable = await db.Accounts.SingleAsync(a => a.CompanyId == company.Id && a.Code == PostingAccountCodes.CodeOf(PostingAccount.Receivable));
        var sales = await db.Accounts.SingleAsync(a => a.CompanyId == company.Id && a.Code == PostingAccountCodes.CodeOf(PostingAccount.Sales));
        var duplicate = new JournalEntry
        {
            CompanyId = company.Id, Date = DateTime.Today, Reference = "DUP-SRC", Description = "second entry",
            SourceType = DocumentTypes.SalesInvoice, SourceId = 501
        };
        duplicate.AddLine(receivable.Id, 1m, 0m);
        duplicate.AddLine(sales.Id, 0m, 1m);
        db.JournalEntries.Add(duplicate);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        // CK_JournalEntry_Source: a type without an id is refused by the table
        using var db2 = new StockiaDbContext(Options());
        var half = new JournalEntry
        {
            CompanyId = company.Id, Date = DateTime.Today, Reference = "HALF-SRC", Description = "type only",
            SourceType = DocumentTypes.SalesInvoice
        };
        half.AddLine(receivable.Id, 1m, 0m);
        half.AddLine(sales.Id, 0m, 1m);
        db2.JournalEntries.Add(half);
        await Assert.ThrowsAsync<DbUpdateException>(() => db2.SaveChangesAsync());
    }
}
