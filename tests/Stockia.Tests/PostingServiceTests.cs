using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Domain;
using Stockia.Web.Services;
using Xunit;

namespace Stockia.Tests;

/// <summary>SR-4 task 13.1 (d2, written first): the posting service turns a document into a balanced entry linked
/// to it, and that entry goes through the same checks as a manual entry. A stub rule gives the lines, so these tests
/// do not depend on the invoice rules (task 13.2).</summary>
public class PostingServiceTests
{
    private sealed class StubRule : IPostingRule
    {
        public string DocumentType => "Test";
        public List<PostingLine> Lines { get; } = new()
        {
            new PostingLine(PostingAccount.Receivable, 111m, 0m),
            new PostingLine(PostingAccount.Sales, 0m, 100m),
            new PostingLine(PostingAccount.VatCollected, 0m, 11m),
        };
        public IReadOnlyList<PostingLine> Build(PostableDocument document) => Lines;
    }

    private sealed class TestClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 18, 10, 15, 0, TimeSpan.Zero);
    }

    private record Setup(PostingService Service, StockiaDbContext Db, StubRule Rule, int A, int B, int User);

    private static async Task<Setup> Build(bool accountsOfAOnly = false)
    {
        var options = new DbContextOptionsBuilder<StockiaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var db = new StockiaDbContext(options);
        var a = new Company { Name = "Company A" };
        var b = new Company { Name = "Company B" };
        db.Companies.AddRange(a, b);
        await db.SaveChangesAsync();

        var user = new AppUser { CompanyId = a.Id, Username = "joy", PasswordHash = "x", DisplayName = "Joy" };
        db.AppUsers.Add(user);
        foreach (var role in Enum.GetValues<PostingAccount>())
        {
            var code = PostingAccountCodes.CodeOf(role);
            db.Accounts.Add(new Account { CompanyId = a.Id, Code = code, Name = role.ToString(), Type = "Asset" });
            if (!accountsOfAOnly)
                db.Accounts.Add(new Account { CompanyId = b.Id, Code = code, Name = role + " B", Type = "Asset" });
        }
        await db.SaveChangesAsync();

        var rule = new StubRule();
        var journal = new JournalService(new EfJournalRepository(db));
        var service = new PostingService(journal, new EfPostedDocuments(db), new IPostingRule[] { rule },
            new AuditService(db, new TestClock()));
        return new Setup(service, db, rule, a.Id, b.Id, user.Id);
    }

    private static PostableDocument Document(int id = 7, string number = "SI-0007", string? description = null) =>
        new("Test", id, number, new DateTime(2026, 11, 18, 14, 30, 0), description, Array.Empty<DocumentLine>());

    // d2: a document becomes a balanced entry linked to it
    [Fact]
    public async Task A_document_becomes_a_balanced_entry_linked_to_it()
    {
        var s = await Build();
        var result = await s.Service.PostAsync(s.A, s.User, Document());

        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        var entry = await s.Db.JournalEntries.Include(e => e.Lines).SingleAsync(e => e.Id == result.EntryId);
        Assert.True(entry.IsBalanced);
        Assert.Equal(111m, entry.TotalDebit);
        Assert.Equal(111m, entry.TotalCredit);
        Assert.Equal(s.A, entry.CompanyId);
        Assert.Equal("Test", entry.SourceType);
        Assert.Equal(7, entry.SourceId);
        Assert.Equal("SI-0007", entry.Reference);
        Assert.Equal(new DateTime(2026, 11, 18), entry.Date);
        Assert.Equal(3, entry.Lines.Count);
        Assert.All(entry.Lines, l => Assert.Equal(s.A, l.CompanyId));
    }

    [Fact]
    public async Task Each_line_goes_to_the_account_of_its_role()
    {
        var s = await Build();
        var result = await s.Service.PostAsync(s.A, s.User, Document());

        var entry = await s.Db.JournalEntries.Include(e => e.Lines).ThenInclude(l => l.Account)
            .SingleAsync(e => e.Id == result.EntryId);
        Assert.Equal(111m, entry.Lines.Single(l => l.Account!.Code == "411").Debit);
        Assert.Equal(100m, entry.Lines.Single(l => l.Account!.Code == "701").Credit);
        Assert.Equal(11m, entry.Lines.Single(l => l.Account!.Code == "4457").Credit);
    }

    // d2: the same checks as a manual entry
    [Fact]
    public async Task An_entry_whose_debits_and_credits_differ_is_refused_like_a_manual_one()
    {
        var s = await Build();
        s.Rule.Lines[2] = new PostingLine(PostingAccount.VatCollected, 0m, 10.99m);

        var result = await s.Service.PostAsync(s.A, s.User, Document());

        Assert.False(result.Succeeded);
        Assert.Equal(0.01m, result.Difference);
        Assert.Contains(result.Errors, e => e.Contains("must be equal"));
        Assert.Empty(s.Db.JournalEntries);
        Assert.Empty(s.Db.AuditLogs);
    }

    [Fact]
    public async Task An_inactive_account_is_refused_like_in_a_manual_entry()
    {
        var s = await Build();
        var sales = await s.Db.Accounts.SingleAsync(a => a.CompanyId == s.A && a.Code == "701");
        sales.IsActive = false;
        await s.Db.SaveChangesAsync();

        var result = await s.Service.PostAsync(s.A, s.User, Document());

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("701") && e.Contains("inactive"));
        Assert.Empty(s.Db.JournalEntries);
    }

    [Fact]
    public async Task A_document_without_a_number_is_refused_like_an_entry_without_a_reference()
    {
        var s = await Build();
        var result = await s.Service.PostAsync(s.A, s.User, Document(number: "  "));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("Reference"));
        Assert.Empty(s.Db.JournalEntries);
    }

    [Fact]
    public async Task An_entry_with_fewer_than_two_lines_is_refused()
    {
        var s = await Build();
        s.Rule.Lines.RemoveRange(1, 2);

        var result = await s.Service.PostAsync(s.A, s.User, Document());

        Assert.False(result.Succeeded);
        Assert.Empty(s.Db.JournalEntries);
    }

    // The chart of accounts must hold the accounts of the rule
    [Fact]
    public async Task A_missing_account_is_named_and_nothing_is_saved()
    {
        var s = await Build();
        s.Db.Accounts.Remove(await s.Db.Accounts.SingleAsync(a => a.CompanyId == s.A && a.Code == "4457"));
        await s.Db.SaveChangesAsync();

        var result = await s.Service.PostAsync(s.A, s.User, Document());

        Assert.False(result.Succeeded);
        var error = Assert.Single(result.Errors);
        Assert.Contains("4457", error);
        Assert.Contains("chart of accounts", error);
        Assert.Empty(s.Db.JournalEntries);
    }

    // d5: company check
    [Fact]
    public async Task Accounts_of_another_company_are_never_used()
    {
        var s = await Build(accountsOfAOnly: true);
        var result = await s.Service.PostAsync(s.B, s.User, Document());   // company B has no accounts at all

        Assert.False(result.Succeeded);
        Assert.Equal(3, result.Errors.Count);
        Assert.Empty(s.Db.JournalEntries);
    }

    [Fact]
    public async Task The_same_document_id_can_be_posted_by_two_companies()
    {
        var s = await Build();
        var first = await s.Service.PostAsync(s.A, s.User, Document());
        var second = await s.Service.PostAsync(s.B, s.User, Document());

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded, string.Join("; ", second.Errors));
        Assert.Equal(2, await s.Db.JournalEntries.CountAsync());
    }

    [Fact]
    public async Task A_document_is_posted_only_once()
    {
        var s = await Build();
        var first = await s.Service.PostAsync(s.A, s.User, Document());
        var second = await s.Service.PostAsync(s.A, s.User, Document());

        Assert.True(first.Succeeded);
        Assert.False(second.Succeeded);
        Assert.Contains(second.Errors, e => e.Contains("already been posted") && e.Contains(first.EntryId.ToString()));
        Assert.Equal(1, await s.Db.JournalEntries.CountAsync());
    }

    [Fact]
    public async Task A_type_without_a_rule_is_refused()
    {
        var s = await Build();
        var result = await s.Service.PostAsync(s.A, s.User, Document() with { Type = "Payment" });

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("no posting rule") && e.Contains("Payment"));
        Assert.Empty(s.Db.JournalEntries);
    }

    // SR-6: the audit trail says who caused the entry
    [Fact]
    public async Task The_audit_trail_records_the_user_and_the_entry()
    {
        var s = await Build();
        var result = await s.Service.PostAsync(s.A, s.User, Document());

        var log = await s.Db.AuditLogs.SingleAsync();
        Assert.Equal(AuditActions.EntryPostedAutomatically, log.Action);
        Assert.Equal(s.User, log.UserId);
        Assert.Equal(s.A, log.CompanyId);
        Assert.Equal(result.EntryId, log.EntryId);
    }

    [Fact]
    public async Task A_very_long_document_note_is_cut_instead_of_refusing_the_posting()
    {
        var s = await Build();
        var result = await s.Service.PostAsync(s.A, s.User, Document(description: new string('x', 600)));

        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        var entry = await s.Db.JournalEntries.SingleAsync();
        Assert.Equal(500, entry.Description.Length);
    }
}
