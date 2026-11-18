using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Domain;
using Stockia.Web.Services;
using Xunit;

namespace Stockia.Tests;

/// <summary>Runs only when STOCKIA_TEST_CONNECTION is set (GitHub Actions, after db/006_journal_v1.sql).
/// Proves the EF model and the real SQL Server tables agree (DECIMAL(18,2), CompanyId, foreign keys).
/// Same collection as the other SQL Server tests, so they never touch the database at the same time.</summary>
[Collection("SqlServer")]
public class JournalSqlServerTests
{
    [SqlServerFact]
    public async Task Three_line_entry_is_saved_in_SQL_Server_and_found_in_each_ledger()
    {
        var options = new DbContextOptionsBuilder<StockiaDbContext>()
            .UseSqlServer(Environment.GetEnvironmentVariable("STOCKIA_TEST_CONNECTION")).Options;
        using var db = new StockiaDbContext(options);

        var (company, error) = await new CompanyService(db)
            .CreateAsync("SQL journal " + Guid.NewGuid().ToString("N")[..8], null, "USD");
        Assert.Null(error);

        var accounts = new[]
        {
            new Account { CompanyId = company!.Id, Code = "1000", Name = "Cash", Type = "Asset" },
            new Account { CompanyId = company.Id, Code = "4000", Name = "Sales", Type = "Revenue" },
            new Account { CompanyId = company.Id, Code = "6000", Name = "Rent", Type = "Expense" },
        };
        db.Accounts.AddRange(accounts);
        await db.SaveChangesAsync();

        var service = new JournalService(new EfJournalRepository(db));
        var reference = "SQL-" + Guid.NewGuid().ToString("N")[..8];
        var result = await service.PostAsync(company.Id, new NewJournalEntry(
            DateTime.Today, reference, "SQL Server test",
            new NewJournalLine[]
            {
                new(accounts[0].Id, 100.10m, 0m),
                new(accounts[1].Id, 0m, 60.05m),
                new(accounts[2].Id, 0m, 40.05m),
            }));

        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        foreach (var a in accounts)
        {
            var ledger = await service.GetLedgerAsync(company.Id, a.Id);
            Assert.Contains(ledger!.Rows, r => r.Reference == reference);
        }
    }
}
