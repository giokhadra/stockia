using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Domain;
using Stockia.Web.Services;
using Xunit;

namespace Stockia.Tests;

/// <summary>Item 11, SR-19 (d2): a foreign amount is converted at the rate of its date, and both the foreign
/// and the base amounts are kept. Written before the code they check.</summary>
public class ExchangeRateTests
{
    private static readonly DateTime Nov1 = new(2026, 11, 1), Nov10 = new(2026, 11, 10), Nov15 = new(2026, 11, 15);

    // ---- the rule: which rate, and the conversion ----

    [Fact]
    public void The_rate_used_is_the_latest_one_on_or_before_the_date()
    {
        var rates = new[]
        {
            new ExchangeRate { Currency = "EUR", Date = Nov1, Rate = 1.08m },
            new ExchangeRate { Currency = "EUR", Date = Nov15, Rate = 1.10m },
            new ExchangeRate { Currency = "LBP", Date = Nov1, Rate = 0.0000111732m },
        };

        Assert.Equal(1.08m, ExchangeRate.On(rates, "EUR", Nov10)!.Rate);
        Assert.Equal(1.10m, ExchangeRate.On(rates, "EUR", Nov15)!.Rate);   // a rate applies from its own date
        Assert.Null(ExchangeRate.On(rates, "EUR", new DateTime(2026, 10, 31)));
        Assert.Null(ExchangeRate.On(rates, "GBP", Nov10));
    }

    [Theory]
    [InlineData("100", "1.085", "108.50")]                // EUR 100 at 1.085
    [InlineData("8950000", "0.0000111732", "100.00")]     // LBP 8,950,000 at 1/89,500
    [InlineData("0.01", "0.5", "0.01")]                   // half a cent rounds away from zero
    public void A_foreign_amount_is_converted_to_the_base_currency_to_the_cent(string foreign, string rate, string expected)
    {
        Assert.Equal(decimal.Parse(expected, CultureInfo.InvariantCulture), ExchangeRate.Convert(decimal.Parse(foreign, CultureInfo.InvariantCulture), decimal.Parse(rate, CultureInfo.InvariantCulture)));
    }

    // ---- the journal: both amounts kept ----

    private sealed class FakeRates : IExchangeRates
    {
        public string Base { get; set; } = "USD";
        public List<ExchangeRate> Rates { get; } = new();
        public Task<string> BaseCurrencyAsync(int companyId) => Task.FromResult(Base);
        public Task<ExchangeRate?> RateOnAsync(int companyId, string currency, DateTime date) =>
            Task.FromResult(ExchangeRate.On(Rates.Where(r => r.CompanyId == companyId), currency, date));
        public Task RememberAsync(int companyId, string currency, DateTime date, decimal rate)
        {
            Rates.RemoveAll(r => r.CompanyId == companyId && r.Currency == currency && r.Date == date.Date);
            Rates.Add(new ExchangeRate { CompanyId = companyId, Currency = currency, Date = date.Date, Rate = rate });
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRepo : IJournalRepository
    {
        public List<Account> Accounts { get; } = new()
        {
            new Account { Id = 1, CompanyId = 1, Code = "1000", Name = "Cash" },
            new Account { Id = 2, CompanyId = 1, Code = "4000", Name = "Sales" },
        };
        public List<JournalEntry> Entries { get; } = new();
        public Task<List<Account>> ListAccountsAsync(int companyId) => Task.FromResult(Accounts.Where(a => a.CompanyId == companyId).ToList());
        public Task<Account?> FindAccountAsync(int companyId, int accountId) =>
            Task.FromResult(Accounts.FirstOrDefault(a => a.CompanyId == companyId && a.Id == accountId));
        public Task AddAsync(JournalEntry entry) { entry.Id = Entries.Count + 1; Entries.Add(entry); return Task.CompletedTask; }
        public Task<JournalEntry?> FindEntryAsync(int companyId, int entryId) =>
            Task.FromResult(Entries.FirstOrDefault(e => e.CompanyId == companyId && e.Id == entryId));
        public Task<List<JournalEntry>> EntriesTouchingAccountAsync(int companyId, int accountId) =>
            Task.FromResult(Entries.Where(e => e.CompanyId == companyId && e.Lines.Any(l => l.AccountId == accountId)).ToList());
    }

    private static (JournalService Journal, FakeRepo Repo, FakeRates Rates) Build()
    {
        var rates = new FakeRates();
        rates.Rates.Add(new ExchangeRate { CompanyId = 1, Currency = "EUR", Date = Nov1, Rate = 1.08m });
        rates.Rates.Add(new ExchangeRate { CompanyId = 1, Currency = "EUR", Date = Nov15, Rate = 1.10m });
        rates.Rates.Add(new ExchangeRate { CompanyId = 2, Currency = "GBP", Date = Nov1, Rate = 1.27m });
        var repo = new FakeRepo();
        return (new JournalService(repo, rates), repo, rates);
    }

    [Fact]
    public async Task A_foreign_line_is_converted_at_the_rate_of_the_entry_date_and_keeps_both_amounts()
    {
        var (journal, repo, _) = Build();

        var result = await journal.PostAsync(1, new NewJournalEntry(Nov10, "FX-1", null, new NewJournalLine[]
        {
            new(1, 100m, 0m, "eur"),          // EUR 100 at the Nov 1 rate (1.08), not the Nov 15 one
            new(2, 0m, 108m),                 // base currency
        }));

        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        var foreign = repo.Entries.Single().Lines.Single(l => l.AccountId == 1);
        Assert.Equal("EUR", foreign.Currency);
        Assert.Equal(100m, foreign.ForeignAmount);
        Assert.Equal(1.08m, foreign.Rate);
        Assert.Equal(108.00m, foreign.Debit);                 // the base amount is what the ledger adds up
        var local = repo.Entries.Single().Lines.Single(l => l.AccountId == 2);
        Assert.Null(local.Currency);
        Assert.Null(local.ForeignAmount);
        Assert.Null(local.Rate);
    }

    [Fact]
    public async Task The_base_currency_typed_on_a_line_is_not_a_foreign_amount()
    {
        var (journal, repo, _) = Build();
        var result = await journal.PostAsync(1, new NewJournalEntry(Nov10, "FX-2", null,
            new NewJournalLine[] { new(1, 50m, 0m, "USD"), new(2, 0m, 50m) }));

        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        Assert.All(repo.Entries.Single().Lines, l => Assert.Null(l.Currency));
    }

    [Fact]
    public async Task A_currency_without_a_rate_on_or_before_the_date_is_refused()
    {
        var (journal, repo, _) = Build();
        var result = await journal.PostAsync(1, new NewJournalEntry(Nov10, "FX-3", null,
            new NewJournalLine[] { new(1, 100m, 0m, "GBP"), new(2, 0m, 127m) }));   // GBP has a rate in company 2 only

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("no GBP rate"));
        Assert.Empty(repo.Entries);
    }

    [Fact]
    public async Task A_currency_code_that_is_not_three_letters_is_refused()
    {
        var (journal, _, _) = Build();
        var result = await journal.PostAsync(1, new NewJournalEntry(Nov10, "FX-4", null,
            new NewJournalLine[] { new(1, 100m, 0m, "EURO"), new(2, 0m, 100m) }));
        Assert.Contains(result.Errors, e => e.Contains("three-letter"));
    }

    [Fact]
    public async Task The_balance_is_checked_on_the_base_amounts()
    {
        var (journal, _, _) = Build();
        var result = await journal.PostAsync(1, new NewJournalEntry(Nov10, "FX-5", null,
            new NewJournalLine[] { new(1, 100m, 0m, "EUR"), new(2, 0m, 100m) }));   // 108.00 against 100.00

        Assert.False(result.Succeeded);
        Assert.Equal(8.00m, result.Difference);
    }

    // ---- item 11b (#100), BRD OI-1: the rate is chosen per transaction; the last rate entered is proposed ----

    [Fact]
    public async Task A_rate_typed_on_a_foreign_line_is_used_and_kept_on_the_line()
    {
        var (journal, repo, _) = Build();
        var result = await journal.PostAsync(1, new NewJournalEntry(Nov10, "FX-11b", null, new NewJournalLine[]
        {
            new(1, 100m, 0m, "EUR", 1.2m),     // typed: 1.2, not the stored 1.08
            new(2, 0m, 120m),
        }));

        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        var line = repo.Entries.Single().Lines.Single(l => l.Currency == "EUR");
        Assert.Equal((100m, 1.2m, 120.00m), (line.ForeignAmount!.Value, line.Rate!.Value, line.Debit));
    }

    [Fact]
    public async Task A_typed_rate_becomes_the_rate_proposed_for_that_date()
    {
        var (journal, _, rates) = Build();
        Assert.True((await journal.PostAsync(1, new NewJournalEntry(Nov10, "FX-11c", null,
            new NewJournalLine[] { new(1, 100m, 0m, "EUR", 1.2m), new(2, 0m, 120m) }))).Succeeded);

        Assert.Equal(1.2m, (await rates.RateOnAsync(1, "EUR", Nov10))!.Rate);     // proposed next time for Nov 10 on
        Assert.Equal(1.08m, ExchangeRate.On(rates.Rates.Where(r => r.CompanyId == 1), "EUR", new DateTime(2026, 11, 9))!.Rate);
    }

    [Fact]
    public async Task A_typed_rate_works_for_a_currency_with_no_stored_rate()
    {
        var (journal, repo, _) = Build();
        var result = await journal.PostAsync(1, new NewJournalEntry(Nov10, "FX-11d", null,
            new NewJournalLine[] { new(1, 100m, 0m, "GBP", 1.27m), new(2, 0m, 127m) }));   // company 1 has no GBP rate

        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        Assert.Equal(127.00m, repo.Entries.Single().Lines.Single(l => l.Currency == "GBP").Debit);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1.08")]
    public async Task A_typed_rate_of_zero_or_less_is_refused(string typed)
    {
        var (journal, repo, rates) = Build();
        var result = await journal.PostAsync(1, new NewJournalEntry(Nov10, "FX-11e", null, new NewJournalLine[]
            { new(1, 100m, 0m, "EUR", decimal.Parse(typed, CultureInfo.InvariantCulture)), new(2, 0m, 108m) }));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("rate must be above zero"));
        Assert.Empty(repo.Entries);
        Assert.Equal(1.08m, (await rates.RateOnAsync(1, "EUR", Nov10))!.Rate);    // a refused entry changes no rate
    }

    // ---- the rates of a company ----

    private static async Task<(ExchangeRateService Service, int A, int B)> BuildService()
    {
        var db = new StockiaDbContext(new DbContextOptionsBuilder<StockiaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var a = new Company { Name = "Rates A", BaseCurrency = "USD" };
        var b = new Company { Name = "Rates B", BaseCurrency = "LBP" };
        db.Companies.AddRange(a, b);
        await db.SaveChangesAsync();
        return (new ExchangeRateService(db), a.Id, b.Id);
    }

    [Fact]
    public async Task A_rate_is_saved_per_company_and_a_second_rate_on_the_same_date_replaces_it()
    {
        var (service, a, b) = await BuildService();

        Assert.Empty(await service.SetAsync(a, "eur", Nov1, 1.08m));
        Assert.Empty(await service.SetAsync(a, "EUR", Nov1, 1.09m));

        var list = await service.ListAsync(a);
        Assert.Equal(1.09m, Assert.Single(list).Rate);
        Assert.Equal("EUR", list[0].Currency);
        Assert.Null(await service.RateOnAsync(b, "EUR", Nov10));       // company B does not see A's rates
        Assert.Equal("LBP", await service.BaseCurrencyAsync(b));
    }

    [Theory]
    [InlineData("US", "1", "three-letter")]
    [InlineData("USD", "1", "base currency")]
    [InlineData("EUR", "0", "above zero")]
    [InlineData("EUR", "-1.5", "above zero")]
    public async Task A_wrong_rate_is_refused_with_the_reason(string currency, string rate, string reason)
    {
        var (service, a, _) = await BuildService();
        var errors = await service.SetAsync(a, currency, Nov1, decimal.Parse(rate, CultureInfo.InvariantCulture));
        Assert.Contains(errors, e => e.Contains(reason));
        Assert.Empty(await service.ListAsync(a));
    }
}
