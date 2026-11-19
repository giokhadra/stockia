using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Domain;
using Stockia.Web.Services;
using Xunit;

namespace Stockia.Tests;

/// <summary>Runs only when STOCKIA_TEST_CONNECTION is set (GitHub Actions, after db/010_exchange_rate_v1.sql).
/// Item 10 (SR-39 / d3): amounts are DECIMAL in the real database and ledger totals equal SQL Server's own sum of
/// the lines. Item 11 (SR-19): a foreign line is saved with its currency, foreign amount, rate and base amount.</summary>
[Collection("SqlServer")]
public class CurrencyAndPrecisionSqlServerTests
{
    private static StockiaDbContext Db() => new(new DbContextOptionsBuilder<StockiaDbContext>()
        .UseSqlServer(Environment.GetEnvironmentVariable("STOCKIA_TEST_CONNECTION")).Options);

    private sealed class Column
    {
        public string Table { get; set; } = "";
        public string Name { get; set; } = "";
        public string Type { get; set; } = "";
        public byte? Precision { get; set; }
        public int? Scale { get; set; }
    }

    [SqlServerFact]
    public async Task Every_amount_column_is_DECIMAL_18_2_rates_are_DECIMAL_20_10_and_no_column_is_floating_point()
    {
        using var db = Db();
        var columns = await db.Database.SqlQueryRaw<Column>(
            @"SELECT TABLE_NAME AS [Table], COLUMN_NAME AS [Name], DATA_TYPE AS [Type],
                     NUMERIC_PRECISION AS [Precision], NUMERIC_SCALE AS [Scale]
              FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'dbo'").ToListAsync();

        Assert.Empty(columns.Where(c => c.Type is "float" or "real" or "money" or "smallmoney"));

        var decimals = columns.Where(c => c.Type is "decimal" or "numeric").ToList();
        Assert.Contains(decimals, c => c.Table == "JournalLine" && c.Name == "Debit");
        foreach (var c in decimals)
        {
            var expected = c.Name == "Rate" ? (20, 10) : (18, 2);
            Assert.True((c.Precision, c.Scale) == ((byte?)expected.Item1, (int?)expected.Item2),
                $"{c.Table}.{c.Name} is DECIMAL({c.Precision},{c.Scale}), expected DECIMAL({expected.Item1},{expected.Item2})");
        }
    }

    [SqlServerFact]
    public async Task Ledger_totals_equal_SQL_Servers_sum_of_the_lines_and_a_foreign_line_keeps_both_amounts()
    {
        using var db = Db();
        var (company, error) = await new CompanyService(db)
            .CreateAsync("SQL currency " + Guid.NewGuid().ToString("N")[..8], null, "USD");
        Assert.Null(error);
        var cash = new Account { CompanyId = company!.Id, Code = "1000", Name = "Cash", Type = "Asset" };
        var bank = new Account { CompanyId = company.Id, Code = "1100", Name = "Bank EUR", Type = "Asset" };
        var sales = new Account { CompanyId = company.Id, Code = "4000", Name = "Sales", Type = "Revenue" };
        db.Accounts.AddRange(cash, bank, sales);
        await db.SaveChangesAsync();

        var rates = new ExchangeRateService(db);
        Assert.Empty(await rates.SetAsync(company.Id, "EUR", new DateTime(2026, 11, 1), 1.0851234567m));
        var journal = new JournalService(new EfJournalRepository(db), rates);

        foreach (var amount in new[] { 0.10m, 0.20m, 1.005m, 33.333m, 999999.99m })
        {
            var r = await journal.PostAsync(company.Id, new NewJournalEntry(new DateTime(2026, 11, 18),
                "SQL-" + Guid.NewGuid().ToString("N")[..8], null,
                new NewJournalLine[] { new(cash.Id, amount, 0m), new(sales.Id, 0m, amount) }));
            Assert.True(r.Succeeded, string.Join("; ", r.Errors));
        }

        var fx = await journal.PostAsync(company.Id, new NewJournalEntry(new DateTime(2026, 11, 18), "SQL-EUR", null,
            new NewJournalLine[] { new(bank.Id, 100m, 0m, "EUR"), new(sales.Id, 0m, 108.51m) }));
        Assert.True(fx.Succeeded, string.Join("; ", fx.Errors));

        db.ChangeTracker.Clear();
        var sqlSum = await db.JournalLines.Where(l => l.AccountId == cash.Id).SumAsync(l => l.Debit);
        var ledger = await journal.GetLedgerAsync(company.Id, cash.Id);
        Assert.Equal(sqlSum, ledger!.TotalDebit);
        Assert.Equal(1000034.63m, ledger.TotalDebit);   // 0.10 + 0.20 + 1.01 + 33.33 + 999,999.99

        var eur = await db.JournalLines.SingleAsync(l => l.AccountId == bank.Id);
        Assert.Equal("EUR", eur.Currency);
        Assert.Equal(100.00m, eur.ForeignAmount);
        Assert.Equal(1.0851234567m, eur.Rate);          // all 10 decimals of the rate survive the round trip
        Assert.Equal(108.51m, eur.Debit);
    }
}
