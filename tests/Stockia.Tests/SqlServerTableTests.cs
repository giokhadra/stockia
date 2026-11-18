using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Services;
using Xunit;

namespace Stockia.Tests;

/// <summary>Runs only when STOCKIA_TEST_CONNECTION is set (GitHub Actions sets it after running the
/// CREATE TABLE script). Proves the EF model and the real SQL Server table agree.</summary>
public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("STOCKIA_TEST_CONNECTION")))
            Skip = "STOCKIA_TEST_CONNECTION is not set (no SQL Server available).";
    }
}

[Collection("SqlServer")]
public class SqlServerTableTests
{
    [SqlServerFact]
    public async Task Script_table_matches_model_and_seeded_user_can_be_found()
    {
        var options = new DbContextOptionsBuilder<StockiaDbContext>()
            .UseSqlServer(Environment.GetEnvironmentVariable("STOCKIA_TEST_CONNECTION")).Options;
        using var db = new StockiaDbContext(options);

        DbSeeder.Seed(db, new BcryptPasswordHasher());   // SR-34: the app's hasher

        var user = await new EfUserRepository(db).FindByUsernameAsync(DbSeeder.SeedUsername);
        Assert.NotNull(user);
        Assert.True(user!.CompanyId > 0);
    }
}
