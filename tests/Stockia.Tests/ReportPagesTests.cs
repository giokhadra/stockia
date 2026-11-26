using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Stockia.Web.Data;
using Stockia.Web.Domain;
using Xunit;

namespace Stockia.Tests;

/// <summary>System test of SR-21 (d3), through the page: the statements of the chosen company for a date range,
/// for every role (read-only page), never another company's figures (d5), and the page requires login.</summary>
public class ReportPagesTests : IClassFixture<StockiaFactory>
{
    private readonly StockiaFactory _factory;
    private readonly AccessSeeding.Data _data;

    public ReportPagesTests(StockiaFactory factory)
    {
        _factory = factory;
        _data = AccessSeeding.Ensure(factory, "report");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockiaDbContext>();
        if (db.Accounts.Any(a => a.CompanyId == _data.CompanyAId && a.Code == "RP-BANK")) return;

        var bank = new Account { CompanyId = _data.CompanyAId, Code = "RP-BANK", Name = "Report bank", Type = "Asset" };
        var capital = new Account { CompanyId = _data.CompanyAId, Code = "RP-CAP", Name = "Report capital", Type = "Equity" };
        var sales = new Account { CompanyId = _data.CompanyAId, Code = "RP-SALES", Name = "Report sales", Type = "Revenue" };
        var secret = new Account { CompanyId = _data.CompanyBId, Code = "RP-B", Name = "B secret revenue", Type = "Revenue" };
        var secretBank = new Account { CompanyId = _data.CompanyBId, Code = "RP-BB", Name = "B secret bank", Type = "Asset" };
        db.Accounts.AddRange(bank, capital, sales, secret, secretBank);
        db.SaveChanges();

        void Post(int company, DateTime date, int debitAccount, int creditAccount, decimal amount)
        {
            var e = new JournalEntry { CompanyId = company, Date = date, Reference = "RP", Description = "report test" };
            e.AddLine(debitAccount, amount, 0m);
            e.AddLine(creditAccount, 0m, amount);
            db.JournalEntries.Add(e);
        }
        Post(_data.CompanyAId, new DateTime(2026, 2, 1), bank.Id, capital.Id, 500m);
        Post(_data.CompanyAId, new DateTime(2026, 3, 1), bank.Id, sales.Id, 250.50m);
        Post(_data.CompanyBId, new DateTime(2026, 3, 1), secretBank.Id, secret.Id, 777m);
        db.SaveChanges();
    }

    private async Task<HttpClient> LoginAsync(string role)
    {
        var client = TestClient.Https(_factory);
        var response = await TestClient.LoginAsync(client, "report-" + role, AccessSeeding.Password);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }

    [Fact]
    public async Task The_accountant_sees_the_three_statements_and_they_balance()
    {
        var client = await LoginAsync("accountant");
        var page = await client.GetStringAsync("/Reports?from=2026-01-01&to=2026-12-31");

        Assert.Contains("Trial balance", page);
        Assert.Contains("Income statement", page);
        Assert.Contains("Balance sheet", page);
        Assert.Contains("id=\"tb-debit\">750.50<", page);
        Assert.Contains("id=\"tb-credit\">750.50<", page);
        Assert.Contains("id=\"net-income\">250.50<", page);
        Assert.Contains("id=\"total-assets\">750.50<", page);
        Assert.Contains("id=\"total-liabilities-equity\">750.50<", page);
        Assert.DoesNotContain("trial-balance-error", page);
        Assert.DoesNotContain("balance-sheet-error", page);
    }

    // SR-33: a read-only page, so the viewer may open it
    [Fact]
    public async Task A_viewer_may_read_the_statements()
    {
        var viewer = await LoginAsync("viewer");
        var response = await viewer.GetAsync("/Reports?from=2026-01-01&to=2026-12-31");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Report sales", await response.Content.ReadAsStringAsync());
    }

    // d5: the company is the signed-in user's, never another one's
    [Fact]
    public async Task Another_companys_figures_never_appear()
    {
        var client = await LoginAsync("accountant");
        var page = await client.GetStringAsync("/Reports?from=2026-01-01&to=2026-12-31");

        Assert.DoesNotContain("B secret", page);
        Assert.DoesNotContain("777.00", page);
    }

    [Fact]
    public async Task A_wrong_range_shows_its_reason()
    {
        var client = await LoginAsync("accountant");
        var page = await client.GetStringAsync("/Reports?from=2026-12-31&to=2026-01-01");

        Assert.Contains("The start date cannot be after the end date.", page);
        Assert.DoesNotContain("id=\"trial-balance\"", page);
    }

    [Fact]
    public async Task Without_dates_the_page_opens_on_this_year()
    {
        var client = await LoginAsync("accountant");
        var response = await client.GetAsync("/Reports");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains($"value=\"{DateTime.Today.Year}-01-01\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_page_requires_login()
    {
        var anonymous = TestClient.Https(_factory);
        var response = await anonymous.GetAsync("/Reports");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Account/Login", LoginRedirect.PathOf(response));
    }
}