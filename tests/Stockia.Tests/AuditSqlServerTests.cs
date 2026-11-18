using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Domain;
using Stockia.Web.Services;
using Xunit;

namespace Stockia.Tests;

/// <summary>Runs only when STOCKIA_TEST_CONNECTION is set (GitHub Actions, after db/008_audit_log_v1.sql).
/// Proves the EF model and the real AuditLog table agree, and that the table itself lets an entry be
/// reversed only once, even when the service is bypassed.</summary>
[Collection("SqlServer")]
public class AuditSqlServerTests
{
    [SqlServerFact]
    public async Task A_reversal_and_its_audit_lines_are_saved_and_the_table_allows_only_one_reversal()
    {
        var options = new DbContextOptionsBuilder<StockiaDbContext>()
            .UseSqlServer(Environment.GetEnvironmentVariable("STOCKIA_TEST_CONNECTION")).Options;
        using var db = new StockiaDbContext(options);
         DbSeeder.Seed(db, new BcryptPasswordHasher());

        var (company, error) = await new CompanyService(db)
            .CreateAsync("SQL audit " + Guid.NewGuid().ToString("N")[..8], null, "USD");
        Assert.Null(error);

        var user = new AppUser
        {
            CompanyId = company!.Id, Username = "sql-audit-" + Guid.NewGuid().ToString("N")[..8],
            PasswordHash = "x", DisplayName = "SQL audit"
        };
        var cash = new Account { CompanyId = company.Id, Code = "1000", Name = "Cash", Type = "Asset" };
        var sales = new Account { CompanyId = company.Id, Code = "4000", Name = "Sales", Type = "Revenue" };
        db.AppUsers.Add(user);
        db.Accounts.AddRange(cash, sales);
        await db.SaveChangesAsync();

        var posted = await new JournalService(new EfJournalRepository(db)).PostAsync(company.Id, new NewJournalEntry(
            DateTime.Today, "SQL-" + Guid.NewGuid().ToString("N")[..8], "SQL Server test",
            new NewJournalLine[] { new(cash.Id, 50.25m, 0m), new(sales.Id, 0m, 50.25m) }));
        Assert.True(posted.Succeeded, string.Join("; ", posted.Errors));

        var reversed = await new JournalReversalService(db, TimeProvider.System)
            .ReverseAsync(company.Id, user.Id, posted.EntryId, DateTime.Today);
        Assert.True(reversed.Succeeded, reversed.Error);

        var history = await new AuditService(db, TimeProvider.System).ForEntryAsync(company.Id, posted.EntryId);
        Assert.Equal(AuditActions.EntryReversed, Assert.Single(history).Action);
        Assert.Equal("SQL audit", history[0].User!.DisplayName);

        // UX_JournalEntry_ReversesEntryId in the table, not only the check in the service
        var second = new JournalEntry
        {
            CompanyId = company.Id, Date = DateTime.Today, Reference = "DUP-REV", Description = "second reversal",
            ReversesEntryId = posted.EntryId
        };
        second.AddLine(cash.Id, 0m, 50.25m);
        second.AddLine(sales.Id, 50.25m, 0m);
        db.JournalEntries.Add(second);
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}