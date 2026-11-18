using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Services;
using Xunit;

namespace Stockia.Tests;

public class CompanyServiceTests
{
    private static (CompanyService Service, StockiaDbContext Db) Build()
    {
        var options = new DbContextOptionsBuilder<StockiaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var db = new StockiaDbContext(options);
        return (new CompanyService(db), db);
    }

    [Fact]
    public async Task Creates_a_company_with_its_base_currency()
    {
        var (service, db) = Build();
        var (company, error) = await service.CreateAsync("Cedar Foods SARL", "1234567", "USD");

        Assert.Null(error);
        Assert.NotNull(company);
        var saved = await db.Companies.SingleAsync();
        Assert.Equal("Cedar Foods SARL", saved.Name);
        Assert.Equal("1234567", saved.TaxNumber);
        Assert.Equal("USD", saved.BaseCurrency);
    }

    [Fact]
    public async Task Refuses_an_empty_name()
    {
        var (service, db) = Build();
        var (company, error) = await service.CreateAsync("  ", null, "USD");

        Assert.Null(company);
        Assert.NotNull(error);
        Assert.Empty(db.Companies);
    }

    [Fact]
    public async Task Refuses_an_unknown_base_currency()
    {
        var (service, db) = Build();
        var (company, error) = await service.CreateAsync("Zahle Textiles SARL", null, "XYZ");

        Assert.Null(company);
        Assert.NotNull(error);
        Assert.Empty(db.Companies);
    }

    [Fact]
    public async Task Lists_companies_by_name()
    {
        var (service, _) = Build();
        await service.CreateAsync("Zahle Textiles SARL", null, "LBP");
        await service.CreateAsync("Byblos Pharma SAL", null, "USD");

        var list = await service.ListAsync();
        Assert.Equal(new[] { "Byblos Pharma SAL", "Zahle Textiles SARL" }, list.Select(c => c.Name));
    }
}