using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stockia.Web.Data;
using Xunit;

namespace Stockia.Tests;

/// <summary>System test of item 2 (SR-18 / UC-14), through the pages: two companies with different
/// base currencies each get their own twelve monthly periods, and nothing of A is listed under B.</summary>
public class CompanyPagesTests : IClassFixture<StockiaFactory>
{
    private readonly StockiaFactory _factory;
    public CompanyPagesTests(StockiaFactory factory) => _factory = factory;

    private static int PeriodRows(string html) => Regex.Matches(html, "<tr class=\"period\">").Count;

    [Fact]
    public async Task Two_companies_with_different_base_currencies_each_get_their_own_twelve_periods()
    {
        var client = TestClient.Https(_factory);
        Assert.Equal(HttpStatusCode.Redirect, (await TestClient.LoginAsync(client)).StatusCode);

        foreach (var (name, currency) in new[] { ("Cedar Foods SARL", "LBP"), ("Byblos Pharma SAL", "USD") })
        {
            var created = await TestClient.PostFormAsync(client, "/Company/Create", new Dictionary<string, string>
            {
                ["Name"] = name, ["TaxNumber"] = "", ["BaseCurrency"] = currency
            });
            Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        }

        var list = await client.GetStringAsync("/Company");
        Assert.Contains("Cedar Foods SARL", list);
        Assert.Contains("Byblos Pharma SAL", list);
        Assert.Contains("LBP", list);
        Assert.Contains("USD", list);

        int idA, idB;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StockiaDbContext>();
            idA = (await db.Companies.SingleAsync(c => c.Name == "Cedar Foods SARL")).Id;
            idB = (await db.Companies.SingleAsync(c => c.Name == "Byblos Pharma SAL")).Id;
        }

        var pageA = await client.GetStringAsync($"/Company/Periods/{idA}");
        var pageB = await client.GetStringAsync($"/Company/Periods/{idB}");
        Assert.Equal(12, PeriodRows(pageA));
        Assert.Equal(12, PeriodRows(pageB));
        Assert.Contains("Cedar Foods SARL", pageA);
        Assert.DoesNotContain("Byblos Pharma SAL", pageA);
        Assert.Contains("LBP", pageA);
        Assert.Contains("USD", pageB);
    }

    [Fact]
    public async Task An_unknown_company_has_no_periods_page()
    {
        var client = TestClient.Https(_factory);
        await TestClient.LoginAsync(client);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/Company/Periods/999999")).StatusCode);
    }
}
