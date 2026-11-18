using Stockia.Web.Domain;
using Stockia.Web.Services;
using Xunit;

namespace Stockia.Tests;

/// <summary>SR-2 / d2 + d5: posting rules and company isolation, with a fake repository (no database).</summary>
public class JournalServiceTests
{
    private class FakeJournalRepo : IJournalRepository
    {
        public List<Account> Accounts { get; } = new();
        public List<JournalEntry> Entries { get; } = new();

        public Task<List<Account>> ListAccountsAsync(int companyId) =>
            Task.FromResult(Accounts.Where(a => a.CompanyId == companyId).OrderBy(a => a.Code).ToList());

        public Task<Account?> FindAccountAsync(int companyId, int accountId) =>
            Task.FromResult(Accounts.FirstOrDefault(a => a.CompanyId == companyId && a.Id == accountId));

        public Task AddAsync(JournalEntry entry)
        {
            entry.Id = Entries.Count + 1;
            Entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<JournalEntry?> FindEntryAsync(int companyId, int entryId) =>
            Task.FromResult(Entries.FirstOrDefault(e => e.CompanyId == companyId && e.Id == entryId));

        public Task<List<JournalEntry>> EntriesTouchingAccountAsync(int companyId, int accountId) =>
            Task.FromResult(Entries.Where(e => e.CompanyId == companyId && e.Lines.Any(l => l.AccountId == accountId)).ToList());
    }

    private static (JournalService service, FakeJournalRepo repo) Build()
    {
        var repo = new FakeJournalRepo();
        repo.Accounts.Add(new Account { Id = 1, CompanyId = 1, Code = "1000", Name = "Cash" });
        repo.Accounts.Add(new Account { Id = 2, CompanyId = 1, Code = "6000", Name = "Rent Expense" });
        repo.Accounts.Add(new Account { Id = 3, CompanyId = 1, Code = "6100", Name = "Utilities Expense" });
        repo.Accounts.Add(new Account { Id = 4, CompanyId = 2, Code = "1000", Name = "Cash (other company)" });
        repo.Accounts.Add(new Account { Id = 5, CompanyId = 1, Code = "9999", Name = "Old account", IsActive = false });
        return (new JournalService(repo), repo);
    }

    private static NewJournalEntry ThreeLines(string? reference = "JE-1") => new(
        new DateTime(2026, 11, 18), reference, "Pay rent and utilities",
        new[]
        {
            new NewJournalLine(2, 800m, 0m),
            new NewJournalLine(3, 150m, 0m),
            new NewJournalLine(1, 0m, 950m),
        });

    [Fact]
    public async Task Saves_a_balanced_three_line_entry_for_the_company()
    {
        var (service, repo) = Build();
        var result = await service.PostAsync(1, ThreeLines());

        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        var saved = Assert.Single(repo.Entries);
        Assert.Equal(1, saved.CompanyId);
        Assert.Equal("JE-1", saved.Reference);
        Assert.Equal(3, saved.Lines.Count);
        Assert.All(saved.Lines, l => Assert.Equal(1, l.CompanyId));
    }

    [Fact]
    public async Task Refuses_an_entry_whose_debits_and_credits_differ()
    {
        var (service, repo) = Build();
        var input = new NewJournalEntry(new DateTime(2026, 11, 18), "JE-1", "",
            new[] { new NewJournalLine(2, 100m, 0m), new NewJournalLine(1, 0m, 99.99m) });

        var result = await service.PostAsync(1, input);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("must be equal"));
        Assert.Empty(repo.Entries);
    }

    [Fact]
    public async Task Refuses_fewer_than_two_lines()
    {
        var (service, repo) = Build();
        var input = new NewJournalEntry(new DateTime(2026, 11, 18), "JE-1", "",
            new[] { new NewJournalLine(2, 100m, 0m) });

        var result = await service.PostAsync(1, input);

        Assert.False(result.Succeeded);
        Assert.Empty(repo.Entries);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Refuses_a_missing_reference(string? reference)
    {
        var (service, repo) = Build();
        var result = await service.PostAsync(1, ThreeLines(reference));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("Reference"));
        Assert.Empty(repo.Entries);
    }

    [Fact]
    public async Task Refuses_a_line_with_both_a_debit_and_a_credit()
    {
        var (service, repo) = Build();
        var input = new NewJournalEntry(new DateTime(2026, 11, 18), "JE-1", "",
            new[] { new NewJournalLine(2, 100m, 100m), new NewJournalLine(1, 0m, 100m) });

        var result = await service.PostAsync(1, input);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.StartsWith("Line 1"));
        Assert.Empty(repo.Entries);
    }

    // SR-1: accounts are deactivated, never deleted; a deactivated account takes no new postings
    [Fact]
    public async Task Refuses_an_inactive_account()
    {
        var (service, repo) = Build();
        var input = new NewJournalEntry(new DateTime(2026, 11, 18), "JE-1", "",
            new[] { new NewJournalLine(5, 100m, 0m), new NewJournalLine(1, 0m, 100m) });

        var result = await service.PostAsync(1, input);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.StartsWith("Line 1") && e.Contains("inactive"));
        Assert.Empty(repo.Entries);
    }

    [Fact]
    public async Task Only_active_accounts_of_the_company_are_offered_for_posting()
    {
        var (service, _) = Build();

        var postable = await service.ListPostableAccountsAsync(1);
        var all = await service.ListAccountsAsync(1);

        Assert.DoesNotContain(postable, a => a.Code == "9999");
        Assert.Contains(all, a => a.Code == "9999");           // its ledger can still be read
        Assert.All(postable, a => Assert.Equal(1, a.CompanyId));
    }

    // d5: company check
    [Fact]
    public async Task Refuses_an_account_that_belongs_to_another_company()
    {
        var (service, repo) = Build();
        var input = new NewJournalEntry(new DateTime(2026, 11, 18), "JE-1", "",
            new[] { new NewJournalLine(4, 100m, 0m), new NewJournalLine(1, 0m, 100m) });

        var result = await service.PostAsync(1, input);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("account not found"));
        Assert.Empty(repo.Entries);
    }

    [Fact]
    public async Task Ledger_of_an_account_of_another_company_is_not_found()
    {
        var (service, _) = Build();
        Assert.Null(await service.GetLedgerAsync(1, 4));
    }

    [Fact]
    public async Task Ledger_shows_the_posted_line_with_totals_and_balance()
    {
        var (service, _) = Build();
        await service.PostAsync(1, ThreeLines());

        var cash = await service.GetLedgerAsync(1, 1);

        Assert.NotNull(cash);
        var row = Assert.Single(cash!.Rows);
        Assert.Equal(950m, row.Credit);
        Assert.Equal(950m, cash.TotalCredit);
        Assert.Equal(-950m, cash.Balance);
    }
}
