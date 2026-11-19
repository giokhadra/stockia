using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Domain;
using Stockia.Web.Services;
using Xunit;

namespace Stockia.Tests;

/// <summary>Item 10, SR-39 (d2): amounts keep two decimals through saving and adding, and ledger totals
/// equal the sum of the lines to 0.01. Written before the code they check.</summary>
public class MoneyPrecisionTests
{
    private static async Task<(StockiaDbContext Db, JournalService Journal, int Company, int Cash, int Sales)> Build()
    {
        var db = new StockiaDbContext(new DbContextOptionsBuilder<StockiaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var company = new Company { Name = "Precision SARL" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var cash = new Account { CompanyId = company.Id, Code = "1000", Name = "Cash", Type = "Asset" };
        var sales = new Account { CompanyId = company.Id, Code = "4000", Name = "Sales", Type = "Revenue" };
        db.Accounts.AddRange(cash, sales);
        await db.SaveChangesAsync();
        return (db, new JournalService(new EfJournalRepository(db)), company.Id, cash.Id, sales.Id);
    }

    [Fact]
    public async Task Amounts_keep_two_decimals_through_saving_and_reading_back()
    {
        var (db, journal, company, cash, sales) = await Build();

        var result = await journal.PostAsync(company, new NewJournalEntry(new DateTime(2026, 11, 19), "P-1", null,
            new NewJournalLine[] { new(cash, 0.10m, 0m), new(cash, 0.20m, 0m), new(sales, 0m, 0.30m) }));
        Assert.True(result.Succeeded, string.Join("; ", result.Errors));

        db.ChangeTracker.Clear();
        var saved = await db.JournalEntries.Include(e => e.Lines).SingleAsync(e => e.Id == result.EntryId);
        Assert.Equal(new[] { 0.10m, 0.20m }, saved.Lines.Where(l => l.Debit > 0).Select(l => l.Debit).OrderBy(d => d));
        Assert.Equal(0.30m, saved.TotalDebit);          // exactly 0.30, never 0.30000000000000004
        Assert.Equal(saved.TotalDebit, saved.TotalCredit);
    }

    [Fact]
    public void A_third_of_a_cent_is_rounded_once_on_the_line_and_the_total_is_the_sum_of_the_lines()
    {
        var entry = new JournalEntry { CompanyId = 1 };
        entry.AddLine(1, 33.333m, 0m);
        entry.AddLine(1, 33.333m, 0m);
        entry.AddLine(1, 33.333m, 0m);

        Assert.All(entry.Lines, l => Assert.Equal(33.33m, l.Debit));
        Assert.Equal(99.99m, entry.TotalDebit);          // what the user sees line by line, not 100.00
    }

    [Fact]
    public async Task Ledger_totals_equal_the_sum_of_the_lines_to_the_cent()
    {
        var (_, journal, company, cash, sales) = await Build();
        var amounts = new[] { 0.01m, 0.10m, 0.20m, 12.345m, 999999.99m, 1.005m, 7.77m };
        var n = 0;
        foreach (var a in amounts)
        {
            var posted = await journal.PostAsync(company, new NewJournalEntry(new DateTime(2026, 11, 19), "L-" + ++n, null,
                new NewJournalLine[] { new(cash, a, 0m), new(sales, 0m, a) }));
            Assert.True(posted.Succeeded, string.Join("; ", posted.Errors));
        }

        var ledger = await journal.GetLedgerAsync(company, cash);
        Assert.NotNull(ledger);
        Assert.Equal(ledger!.Rows.Sum(r => r.Debit), ledger.TotalDebit);
        Assert.Equal(amounts.Select(Money.Round).Sum(), ledger.TotalDebit);
        Assert.Equal(ledger.TotalDebit - ledger.TotalCredit, ledger.Rows[^1].Balance);  // running balance ends on the total
        Assert.All(ledger.Rows, r => Assert.Equal(r.Debit, Math.Round(r.Debit, 2)));
    }
}
