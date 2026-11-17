using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Domain;
using Stockia.Web.Services;
using Xunit;

namespace Stockia.Tests;

/// <summary>Unit tests of item 2 (SR-18 / UC-14), task 2.1: a new company gets its twelve monthly periods.</summary>
public class FiscalPeriodTests
{
    private sealed class FixedClock : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedClock(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    // 2028 is a leap year: February must end on the 29th.
    private static (CompanyService Service, StockiaDbContext Db) Build(int year = 2028)
    {
        var options = new DbContextOptionsBuilder<StockiaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var db = new StockiaDbContext(options);
        var clock = new FixedClock(new DateTimeOffset(year, 3, 15, 9, 0, 0, TimeSpan.Zero));
        return (new CompanyService(db, clock), db);
    }

    [Fact]
    public async Task A_new_company_gets_twelve_monthly_periods()
    {
        var (service, db) = Build();
        var (company, error) = await service.CreateAsync("Cedar Foods SARL", null, "LBP");

        Assert.Null(error);
        Assert.Equal(12, await db.FiscalPeriods.CountAsync(p => p.CompanyId == company!.Id));
    }

    [Fact]
    public async Task Periods_run_back_to_back_from_1_January_to_31_December_of_the_current_year()
    {
        var (service, _) = Build(2028);
        var (company, _) = await service.CreateAsync("Cedar Foods SARL", null, "LBP");

        var periods = await service.PeriodsOfAsync(company!.Id);

        Assert.Equal(new DateOnly(2028, 1, 1), periods[0].StartDate);
        Assert.Equal(new DateOnly(2028, 12, 31), periods[^1].EndDate);
        Assert.Equal(new DateOnly(2028, 2, 29), periods[1].EndDate);
        for (var i = 1; i < periods.Count; i++)
            Assert.Equal(periods[i - 1].EndDate.AddDays(1), periods[i].StartDate);   // no gap, no overlap
    }

    [Fact]
    public async Task New_periods_are_open()
    {
        var (service, _) = Build();
        var (company, _) = await service.CreateAsync("Cedar Foods SARL", null, "LBP");

        Assert.All(await service.PeriodsOfAsync(company!.Id), p => Assert.Equal(FiscalPeriod.Open, p.Status));
    }

    [Fact]
    public async Task Each_company_gets_its_own_twelve_periods()
    {
        var (service, db) = Build();
        var (a, _) = await service.CreateAsync("Company A", null, "LBP");
        var (b, _) = await service.CreateAsync("Company B", null, "USD");

        Assert.Equal(24, await db.FiscalPeriods.CountAsync());
        Assert.All(await service.PeriodsOfAsync(a!.Id), p => Assert.Equal(a.Id, p.CompanyId));
        Assert.All(await service.PeriodsOfAsync(b!.Id), p => Assert.Equal(b.Id, p.CompanyId));
        Assert.Equal(12, (await service.PeriodCountsAsync())[a.Id]);
        Assert.Equal(12, (await service.PeriodCountsAsync())[b.Id]);
    }

    [Fact]
    public async Task A_refused_company_gets_no_periods()
    {
        var (service, db) = Build();
        var (company, error) = await service.CreateAsync("  ", null, "LBP");

        Assert.Null(company);
        Assert.NotNull(error);
        Assert.Empty(db.FiscalPeriods);
    }

    [Fact]
    public void MonthsOf_builds_twelve_months_that_each_end_on_their_last_day()
    {
        var periods = FiscalPeriod.MonthsOf(new Company { Name = "X" }, 2027);

        Assert.Equal(12, periods.Count);
        Assert.Equal(new DateOnly(2027, 2, 28), periods[1].EndDate);
        Assert.Equal(new DateOnly(2027, 4, 30), periods[3].EndDate);
        Assert.All(periods, p => Assert.Equal(p.StartDate.AddMonths(1).AddDays(-1), p.EndDate));
    }
}
