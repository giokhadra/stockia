using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Services;
using Xunit;

namespace Stockia.Tests;

/// <summary>Runs on the real SQL Server in CI, after every script in db/ (see SqlServerFact).
/// Same collection as SqlServerTableTests, so the two never seed the database at the same time.</summary>
[Collection("SqlServer")]
public class SqlServerSecurityAndPeriodsTests
{
    private static StockiaDbContext Db() => new(new DbContextOptionsBuilder<StockiaDbContext>()
        .UseSqlServer(Environment.GetEnvironmentVariable("STOCKIA_TEST_CONNECTION")).Options);

    // System test of item 4 (SR-34): the AppUser table holds a bcrypt hash, never the password.
    [SqlServerFact]
    public async Task The_AppUser_table_holds_a_bcrypt_hash_never_the_password()
    {
        using var db = Db();
        DbSeeder.Seed(db, new BcryptPasswordHasher());

        var stored = await db.AppUsers.Where(u => u.Username == DbSeeder.SeedUsername)
            .Select(u => u.PasswordHash).SingleAsync();

        Assert.Matches(@"^\$2[aby]\$12\$", stored);
        Assert.DoesNotContain(DbSeeder.SeedPassword, stored);
    }

    // Item 2 (SR-18) on SQL Server: the FiscalPeriod table matches the model,
    // and a company and its twelve periods are stored together.
    [SqlServerFact]
    public async Task A_company_created_on_SQL_Server_stores_its_twelve_periods()
    {
        using var db = Db();
        var name = "SQL test company " + Guid.NewGuid().ToString("N")[..8];

        var (company, error) = await new CompanyService(db).CreateAsync(name, null, "LBP");

        Assert.Null(error);
        var periods = await db.FiscalPeriods.Where(p => p.CompanyId == company!.Id)
            .OrderBy(p => p.StartDate).ToListAsync();
        Assert.Equal(12, periods.Count);
        Assert.Equal(1, periods[0].StartDate.Day);
        Assert.Equal(1, periods[0].StartDate.Month);
        Assert.Equal(new DateOnly(periods[0].StartDate.Year, 12, 31), periods[^1].EndDate);
    }
}
