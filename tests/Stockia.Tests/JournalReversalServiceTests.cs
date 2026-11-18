using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Domain;
using Stockia.Web.Services;
using Xunit;

namespace Stockia.Tests;

/// <summary>SR-6 (d2): a posted entry is corrected by a reversal, never deleted.</summary>
public class JournalReversalServiceTests
{
    private sealed class TestClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 11, 18, 10, 15, 0, TimeSpan.Zero);
    }

    private record Setup(JournalReversalService Service, StockiaDbContext Db, int A, int B, int User,
        JournalEntry Entry, Account Cash, Account Sales, Account Rent);

    private static async Task<Setup> Build()
    {
        var options = new DbContextOptionsBuilder<StockiaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var db = new StockiaDbContext(options);
        var a = new Company { Name = "Company A" };
        var b = new Company { Name = "Company B" };
        db.Companies.AddRange(a, b);
        await db.SaveChangesAsync();

        var user = new AppUser { CompanyId = a.Id, Username = "joy", PasswordHash = "x", DisplayName = "Joy" };
        var cash = new Account { CompanyId = a.Id, Code = "1000", Name = "Cash", Type = "Asset" };
        var sales = new Account { CompanyId = a.Id, Code = "4000", Name = "Sales", Type = "Revenue" };
        var rent = new Account { CompanyId = a.Id, Code = "6000", Name = "Rent", Type = "Expense" };
        db.AppUsers.Add(user);
        db.Accounts.AddRange(cash, sales, rent);
        await db.SaveChangesAsync();

        var entry = new JournalEntry
        {
            CompanyId = a.Id, Date = new DateTime(2026, 11, 10), Reference = "JE-001", Description = "Original"
        };
        entry.AddLine(cash.Id, 100.10m, 0m);
        entry.AddLine(sales.Id, 0m, 60.05m);
        entry.AddLine(rent.Id, 0m, 40.05m);
        db.JournalEntries.Add(entry);
        await db.SaveChangesAsync();

        return new Setup(new JournalReversalService(db, new TestClock()), db, a.Id, b.Id, user.Id, entry, cash, sales, rent);
    }

    [Fact]
    public async Task The_reversal_swaps_every_debit_and_credit_and_is_balanced()
    {
        var s = await Build();
        var result = await s.Service.ReverseAsync(s.A, s.User, s.Entry.Id, new DateTime(2026, 11, 18, 14, 30, 0));

        Assert.True(result.Succeeded);
        var reversal = await s.Db.JournalEntries.Include(e => e.Lines).SingleAsync(e => e.Id == result.EntryId);
        Assert.Equal("REV-JE-001", reversal.Reference);
        Assert.Equal("Reversal of JE-001", reversal.Description);
        Assert.Equal(s.Entry.Id, reversal.ReversesEntryId);
        Assert.Equal(new DateTime(2026, 11, 18), reversal.Date);
        Assert.True(reversal.IsBalanced);

        Assert.Equal(0m, reversal.Lines.Single(l => l.AccountId == s.Cash.Id).Debit);
        Assert.Equal(100.10m, reversal.Lines.Single(l => l.AccountId == s.Cash.Id).Credit);
        Assert.Equal(60.05m, reversal.Lines.Single(l => l.AccountId == s.Sales.Id).Debit);
        Assert.Equal(40.05m, reversal.Lines.Single(l => l.AccountId == s.Rent.Id).Debit);
    }

    // BRD SR-6: corrected by reversal, not deletion
    [Fact]
    public async Task The_original_entry_stays_and_the_two_entries_cancel_out_on_every_account()
    {
        var s = await Build();
        await s.Service.ReverseAsync(s.A, s.User, s.Entry.Id, new DateTime(2026, 11, 18));

        Assert.Equal(2, await s.Db.JournalEntries.CountAsync());
        var original = await s.Db.JournalEntries.Include(e => e.Lines).SingleAsync(e => e.Id == s.Entry.Id);
        Assert.Equal(3, original.Lines.Count);
        Assert.Equal(100.10m, original.TotalDebit);

        var lines = await s.Db.JournalLines.ToListAsync();
        foreach (var account in lines.GroupBy(l => l.AccountId))
            Assert.Equal(0m, account.Sum(l => l.Debit - l.Credit));
    }

    [Fact]
    public async Task Both_entries_get_an_audit_line_with_the_user()
    {
        var s = await Build();
        var result = await s.Service.ReverseAsync(s.A, s.User, s.Entry.Id, new DateTime(2026, 11, 18));

        var logs = await s.Db.AuditLogs.OrderBy(l => l.Id).ToListAsync();
        Assert.Equal(2, logs.Count);
        Assert.All(logs, l => Assert.Equal(s.User, l.UserId));
        Assert.All(logs, l => Assert.Equal(s.A, l.CompanyId));

        var onOriginal = logs.Single(l => l.EntryId == s.Entry.Id);
        Assert.Equal(AuditActions.EntryReversed, onOriginal.Action);
        Assert.Equal("Reversed by REV-JE-001", onOriginal.Details);

        var onReversal = logs.Single(l => l.EntryId == result.EntryId);
        Assert.Equal(AuditActions.ReversalPosted, onReversal.Action);
        Assert.Equal("Reverses JE-001", onReversal.Details);
    }

    [Fact]
    public async Task An_entry_cannot_be_reversed_twice()
    {
        var s = await Build();
        var first = await s.Service.ReverseAsync(s.A, s.User, s.Entry.Id, new DateTime(2026, 11, 18));
        var second = await s.Service.ReverseAsync(s.A, s.User, s.Entry.Id, new DateTime(2026, 11, 19));

        Assert.True(first.Succeeded);
        Assert.False(second.Succeeded);
        Assert.Contains("already been reversed", second.Error);
        Assert.Equal(2, await s.Db.JournalEntries.CountAsync());
        Assert.Equal(2, await s.Db.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task A_reversal_cannot_itself_be_reversed()
    {
        var s = await Build();
        var first = await s.Service.ReverseAsync(s.A, s.User, s.Entry.Id, new DateTime(2026, 11, 18));
        var again = await s.Service.ReverseAsync(s.A, s.User, first.EntryId, new DateTime(2026, 11, 19));

        Assert.False(again.Succeeded);
        Assert.Contains("cannot be reversed", again.Error);
        Assert.Equal(2, await s.Db.JournalEntries.CountAsync());
    }

    // d5: an entry of another company does not exist for this user
    [Fact]
    public async Task An_entry_of_another_company_is_not_found_and_nothing_is_written()
    {
        var s = await Build();
        var result = await s.Service.ReverseAsync(s.B, s.User, s.Entry.Id, new DateTime(2026, 11, 18));

        Assert.False(result.Succeeded);
        Assert.Equal("Journal entry not found.", result.Error);
        Assert.Equal(1, await s.Db.JournalEntries.CountAsync());
        Assert.Empty(s.Db.AuditLogs);
    }

    [Fact]
    public async Task An_account_deactivated_since_does_not_stop_the_reversal()
    {
        var s = await Build();
        s.Cash.IsActive = false;
        await s.Db.SaveChangesAsync();

        var result = await s.Service.ReverseAsync(s.A, s.User, s.Entry.Id, new DateTime(2026, 11, 18));

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task A_long_reference_is_cut_to_50_characters()
    {
        var s = await Build();
        s.Entry.Reference = new string('R', 50);
        await s.Db.SaveChangesAsync();

        var result = await s.Service.ReverseAsync(s.A, s.User, s.Entry.Id, new DateTime(2026, 11, 18));

        var reversal = await s.Db.JournalEntries.SingleAsync(e => e.Id == result.EntryId);
        Assert.Equal(50, reversal.Reference.Length);
        Assert.StartsWith("REV-", reversal.Reference);
    }

    [Fact]
    public async Task Finds_the_entry_that_reverses_another()
    {
        var s = await Build();
        Assert.Null(await s.Service.FindReversalIdAsync(s.A, s.Entry.Id));

        var result = await s.Service.ReverseAsync(s.A, s.User, s.Entry.Id, new DateTime(2026, 11, 18));

        Assert.Equal(result.EntryId, await s.Service.FindReversalIdAsync(s.A, s.Entry.Id));
        Assert.Null(await s.Service.FindReversalIdAsync(s.B, s.Entry.Id));
    }
}