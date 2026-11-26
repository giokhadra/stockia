using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Domain;
using Stockia.Web.Services;
using Xunit;

namespace Stockia.Tests;

/// <summary>Runs only when STOCKIA_TEST_CONNECTION is set (GitHub Actions). Proves the report query (a join of
/// journal lines and entries, grouped by account) runs on SQL Server itself and gives exact DECIMAL totals.</summary>
[Collection("SqlServer")]
public class ReportSqlServerTests
{
    [SqlServerFact]
    public async Task The_statements_are_computed_by_SQL_Server_to_the_cent()
    {
        var options = new DbContextOptionsBuilder<StockiaDbContext>()
            .UseSqlServer(Environment.GetEnvironmentVariable("STOCKIA_TEST_CONNECTION")).Options;
        using var db = new StockiaDbContext(options);
        DbSeeder.Seed(db, new BcryptPasswordHasher());   // the seeder skips a database that already has users, so run it first

        var company = new Company { Name = "SQL report " + Guid.NewGuid().ToString("N")[..8], BaseCurrency = "USD" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var bank = new Account { CompanyId = company.Id, Code = "5121", Name = "Bank", Type = "Asset" };
        var capital = new Account { CompanyId = company.Id, Code = "101", Name = "Capital", Type = "Equity" };
        var sales = new Account { CompanyId = company.Id, Code = "701", Name = "Sales", Type = "Revenue" };
        db.Accounts.AddRange(bank, capital, sales);
        await db.SaveChangesAsync();

        for (var i = 0; i < 3; i++)   // three sales of 0.10: exactly 0.30, never 0.30000000000000004
        {
            var e = new JournalEntry { CompanyId = company.Id, Date = new DateTime(2026, 3, 1 + i), Reference = "S" + i, Description = "sale" };
            e.AddLine(bank.Id, 0.10m, 0m);
            e.AddLine(sales.Id, 0m, 0.10m);
            db.JournalEntries.Add(e);
        }
        var opening = new JournalEntry { CompanyId = company.Id, Date = new DateTime(2025, 12, 31), Reference = "OPEN", Description = "capital" };
        opening.AddLine(bank.Id, 1000m, 0m);
        opening.AddLine(capital.Id, 0m, 1000m);
        db.JournalEntries.Add(opening);
        await db.SaveChangesAsync();

        using var db2 = new StockiaDbContext(options);
        var (statements, error) = await new ReportService(db2).GenerateAsync(company.Id, new DateTime(2026, 1, 1), new DateTime(2026, 12, 31));

        Assert.Null(error);
        Assert.Equal(0.30m, statements!.TotalDebit);          // only the movements of 2026
        Assert.True(statements.TrialBalanceBalances);
        Assert.Equal(0.30m, statements.NetIncome);
        Assert.Equal(1000.30m, statements.TotalAssets);       // the balance sheet includes the 2025 capital
        Assert.True(statements.BalanceSheetBalances);
    }
}