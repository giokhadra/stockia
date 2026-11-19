using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stockia.Web.Data;
using Stockia.Web.Domain;
using Xunit;

namespace Stockia.Tests;

/// <summary>System test of item 11 (SR-19 / d3), through the pages: rates are recorded on the Exchange rates page,
/// and one entry with EUR and LBP lines is posted at those rates in the base currency (USD) and shows both
/// amounts. Also d5: a viewer cannot record a rate, and another company's rates are never used.</summary>
public class CurrencyFlowTests : IClassFixture<StockiaFactory>
{
    private readonly StockiaFactory _factory;
    private readonly AccessSeeding.Data _data;

    public CurrencyFlowTests(StockiaFactory factory)
    {
        _factory = factory;
        _data = AccessSeeding.Ensure(factory, "fx");
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockiaDbContext>();
        foreach (var (code, name) in new[] { ("FX-CASH", "Cash in LBP"), ("FX-BANK", "Bank in EUR"), ("FX-REV", "Revenue") })
            if (!db.Accounts.Any(a => a.CompanyId == _data.CompanyAId && a.Code == code))
                db.Accounts.Add(new Account { CompanyId = _data.CompanyAId, Code = code, Name = name, Type = "Asset" });
        db.SaveChanges();
    }

    private int AccountId(string code)
    {
        using var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<StockiaDbContext>()
            .Accounts.Single(a => a.CompanyId == _data.CompanyAId && a.Code == code).Id;
    }

    private async Task<HttpClient> LoginAsync(string role)
    {
        var client = TestClient.Https(_factory);
        Assert.Equal(HttpStatusCode.Redirect,
            (await TestClient.LoginAsync(client, "fx-" + role, AccessSeeding.Password)).StatusCode);
        return client;
    }

    private static Task<HttpResponseMessage> SetRate(HttpClient client, string currency, string date, string rate) =>
        TestClient.PostFormAsync(client, "/ExchangeRates", "/ExchangeRates/Set", new Dictionary<string, string>
        {
            ["Currency"] = currency, ["Date"] = date, ["Rate"] = rate
        });

    [Fact]
    public async Task Usd_and_lbp_and_eur_in_one_entry_are_posted_in_the_base_currency_and_show_both_amounts()
    {
        var client = await LoginAsync("accountant");
        Assert.Equal(HttpStatusCode.Redirect, (await SetRate(client, "EUR", "2026-11-01", "1.085")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await SetRate(client, "LBP", "2026-11-01", "0.0000111732")).StatusCode);
        var page = await client.GetStringAsync("/ExchangeRates");
        Assert.Contains("1.085", page);
        Assert.Contains("0.0000111732", page);

        var reference = "FX-" + Guid.NewGuid().ToString("N")[..8];
        var post = await TestClient.PostFormAsync(client, "/Journal/New", new Dictionary<string, string>
        {
            ["Date"] = "2026-11-18", ["Reference"] = reference, ["Description"] = "Currency system test",
            ["Lines[0].AccountId"] = AccountId("FX-CASH").ToString(), ["Lines[0].Currency"] = "LBP",
            ["Lines[0].Debit"] = "8950000", ["Lines[0].Credit"] = "",
            ["Lines[1].AccountId"] = AccountId("FX-BANK").ToString(), ["Lines[1].Currency"] = "EUR",
            ["Lines[1].Debit"] = "100", ["Lines[1].Credit"] = "",
            ["Lines[2].AccountId"] = AccountId("FX-REV").ToString(), ["Lines[2].Currency"] = "",
            ["Lines[2].Debit"] = "", ["Lines[2].Credit"] = "208.50",
        });
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        var details = await client.GetStringAsync(LoginRedirect.PathOf(post));

        // both amounts are shown: the foreign amount with its rate, and the base amount
        Assert.Contains("8,950,000.00", details);
        Assert.Contains("100.00", details);
        Assert.Contains("108.50", details);
        Assert.Contains("208.50", details);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockiaDbContext>();
        var lines = db.JournalLines.Where(l => db.JournalEntries.Any(e => e.Id == l.JournalEntryId && e.Reference == reference)).ToList();
        var eur = lines.Single(l => l.Currency == "EUR");
        Assert.Equal((100m, 1.085m, 108.50m), (eur.ForeignAmount!.Value, eur.Rate!.Value, eur.Debit));
        var lbp = lines.Single(l => l.Currency == "LBP");
        Assert.Equal((8950000m, 100.00m), (lbp.ForeignAmount!.Value, lbp.Debit));
        Assert.Null(lines.Single(l => l.Credit > 0).Currency);
    }

    [Fact]
    public async Task A_viewer_sees_the_rates_but_cannot_record_one()
    {
        var client = await LoginAsync("viewer");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/ExchangeRates")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await SetRate(client, "GBP", "2026-11-01", "1.27")).StatusCode);

        using var scope = _factory.Services.CreateScope();
        Assert.False(scope.ServiceProvider.GetRequiredService<StockiaDbContext>()
            .ExchangeRates.Any(r => r.CompanyId == _data.CompanyAId && r.Currency == "GBP"));
    }

    [Fact]
    public async Task A_rate_of_another_company_is_not_used()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StockiaDbContext>();
            if (!db.ExchangeRates.Any(r => r.CompanyId == _data.CompanyBId && r.Currency == "CHF"))
            {
                db.ExchangeRates.Add(new ExchangeRate
                {
                    CompanyId = _data.CompanyBId, Currency = "CHF", Date = new DateTime(2026, 11, 1), Rate = 1.13m
                });
                db.SaveChanges();
            }
        }

        var client = await LoginAsync("accountant");
        var response = await TestClient.PostFormAsync(client, "/Journal/New", new Dictionary<string, string>
        {
            ["Date"] = "2026-11-18", ["Reference"] = "FX-CHF", ["Description"] = "",
            ["Lines[0].AccountId"] = AccountId("FX-BANK").ToString(), ["Lines[0].Currency"] = "CHF",
            ["Lines[0].Debit"] = "100", ["Lines[0].Credit"] = "",
            ["Lines[1].AccountId"] = AccountId("FX-REV").ToString(), ["Lines[1].Currency"] = "",
            ["Lines[1].Debit"] = "", ["Lines[1].Credit"] = "113.00",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);           // the page comes back with the error
        Assert.Contains("no CHF rate", await response.Content.ReadAsStringAsync());
    }
}
