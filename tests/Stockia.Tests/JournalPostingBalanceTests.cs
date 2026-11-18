using Stockia.Web.Domain;
using Stockia.Web.Services;
using Xunit;

namespace Stockia.Tests;

/// <summary>SR-3 / d2, written before the code (task 8.1): posting refuses an entry whose debits differ
/// from its credits, and the refusal reports the difference. Fake repository, no database.</summary>
public class JournalPostingBalanceTests
{
    private class FakeRepo : IJournalRepository
    {
        public List<Account> Accounts { get; } = new()
        {
            new Account { Id = 1, CompanyId = 1, Code = "1000", Name = "Cash" },
            new Account { Id = 2, CompanyId = 1, Code = "6000", Name = "Rent Expense" },
            new Account { Id = 3, CompanyId = 1, Code = "6100", Name = "Utilities Expense" },
        };
        public List<JournalEntry> Entries { get; } = new();

        public Task<List<Account>> ListAccountsAsync(int companyId) =>
            Task.FromResult(Accounts.Where(a => a.CompanyId == companyId).ToList());
        public Task<Account?> FindAccountAsync(int companyId, int accountId) =>
            Task.FromResult(Accounts.FirstOrDefault(a => a.CompanyId == companyId && a.Id == accountId));
        public Task AddAsync(JournalEntry entry) { entry.Id = Entries.Count + 1; Entries.Add(entry); return Task.CompletedTask; }
        public Task<JournalEntry?> FindEntryAsync(int companyId, int entryId) =>
            Task.FromResult(Entries.FirstOrDefault(e => e.CompanyId == companyId && e.Id == entryId));
        public Task<List<JournalEntry>> EntriesTouchingAccountAsync(int companyId, int accountId) =>
            Task.FromResult(Entries.Where(e => e.CompanyId == companyId && e.Lines.Any(l => l.AccountId == accountId)).ToList());
    }

    private static (JournalService service, FakeRepo repo) Build()
    {
        var repo = new FakeRepo();
        return (new JournalService(repo), repo);
    }

    private static NewJournalEntry TwoLines(decimal debit, decimal credit) => new(
        new DateTime(2026, 11, 18), "JE-1", "Test",
        new[] { new NewJournalLine(2, debit, 0m), new NewJournalLine(1, 0m, credit) });

    public static IEnumerable<object[]> UnbalancedTotals => new[]
    {
        new object[] { 100.00m, 99.99m },     // debits higher by one cent
        new object[] { 100.00m, 100.01m },    // credits higher by one cent
        new object[] { 950.00m, 900.50m },
        new object[] { 10.00m, 1000.00m },
    };

    [Theory]
    [MemberData(nameof(UnbalancedTotals))]
    public async Task Posting_refuses_an_entry_whose_debits_differ_from_its_credits(decimal debit, decimal credit)
    {
        var (service, repo) = Build();

        var result = await service.PostAsync(1, TwoLines(debit, credit));

        Assert.False(result.Succeeded);
        Assert.Empty(repo.Entries);   // nothing is saved
    }

    [Theory]
    [MemberData(nameof(UnbalancedTotals))]
    public async Task The_refusal_reports_the_difference(decimal debit, decimal credit)
    {
        var (service, _) = Build();

        var result = await service.PostAsync(1, TwoLines(debit, credit));

        Assert.Equal(debit - credit, result.Difference);
        var expected = Math.Abs(debit - credit).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains(result.Errors, e => e.Contains("The difference is " + expected));
    }

    [Fact]
    public async Task Three_line_entry_that_does_not_balance_is_refused_with_its_difference()
    {
        var (service, repo) = Build();
        var input = new NewJournalEntry(new DateTime(2026, 11, 18), "JE-1", "Pay rent and utilities",
            new[]
            {
                new NewJournalLine(2, 800m, 0m),
                new NewJournalLine(3, 150m, 0m),
                new NewJournalLine(1, 0m, 900m),
            });

        var result = await service.PostAsync(1, input);

        Assert.False(result.Succeeded);
        Assert.Equal(50m, result.Difference);
        Assert.Contains(result.Errors, e => e.Contains("The difference is 50.00"));
        Assert.Empty(repo.Entries);
    }

    [Fact]
    public async Task A_balanced_entry_is_posted_and_has_no_difference()
    {
        var (service, repo) = Build();

        var result = await service.PostAsync(1, TwoLines(100m, 100m));

        Assert.True(result.Succeeded);
        Assert.Null(result.Difference);
        Assert.Single(repo.Entries);
    }

    // d6 / SR-39: balance is judged on the rounded lines, to 0.01
    [Fact]
    public async Task Amounts_that_round_to_the_same_cent_are_balanced()
    {
        var (service, repo) = Build();

        var result = await service.PostAsync(1, TwoLines(100.004m, 100.00m));

        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        Assert.Single(repo.Entries);
    }
}
