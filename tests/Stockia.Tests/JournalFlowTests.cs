using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Stockia.Web.Data;
using Stockia.Web.Domain;
using Xunit;

namespace Stockia.Tests;

/// <summary>System test of item 7 (SR-2 / d3), through the pages: an entry with 3 or more lines is saved and
/// appears in the ledger of each account used. Also d5: company isolation, and SR-33 roles (a viewer reads, never writes).</summary>
public class JournalFlowTests : IClassFixture<StockiaFactory>
{
    private readonly StockiaFactory _factory;
    private readonly AccessSeeding.Data _data;

    private static readonly (string Code, string Name, bool Active)[] Accounts =
    {
        ("J-CASH", "Journal cash", true),
        ("J-AR", "Journal receivable", true),
        ("J-AP", "Journal payable", true),
        ("J-REV", "Journal revenue", true),
        ("J-RENT", "Journal rent", true),
        ("J-UTIL", "Journal utilities", true),
        ("J-OLD", "Journal old account", false),
    };

    public JournalFlowTests(StockiaFactory factory)
    {
        _factory = factory;
        _data = AccessSeeding.Ensure(factory, "journal");
        EnsureAccounts();
    }

    private void EnsureAccounts()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockiaDbContext>();
        foreach (var (code, name, active) in Accounts)
            if (!db.Accounts.Any(a => a.CompanyId == _data.CompanyAId && a.Code == code))
                db.Accounts.Add(new Account
                {
                    CompanyId = _data.CompanyAId, Code = code, Name = name, Type = "Asset", IsActive = active
                });
        db.SaveChanges();
    }

    private int AccountId(string code)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockiaDbContext>();
        return db.Accounts.Single(a => a.CompanyId == _data.CompanyAId && a.Code == code).Id;
    }

    private async Task<HttpClient> LoginAsync(string role)
    {
        var client = TestClient.Https(_factory);
        var response = await TestClient.LoginAsync(client, "journal-" + role, AccessSeeding.Password);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }

    private static Dictionary<string, string> EntryFields(string reference,
        params (int account, string debit, string credit)[] lines)
    {
        var fields = new Dictionary<string, string>
        {
            ["Date"] = "2026-11-18",
            ["Reference"] = reference,
            ["Description"] = "System test"
        };
        for (var i = 0; i < lines.Length; i++)
        {
            fields[$"Lines[{i}].AccountId"] = lines[i].account.ToString();
            fields[$"Lines[{i}].Debit"] = lines[i].debit;
            fields[$"Lines[{i}].Credit"] = lines[i].credit;
        }
        return fields;
    }

    private static string NewReference() => "JE-" + Guid.NewGuid().ToString("N")[..8];

    private bool EntryExists(string reference)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockiaDbContext>();
        return db.JournalEntries.Any(e => e.Reference == reference);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("chief")]
    [InlineData("accountant")]
    public async Task Three_line_entry_is_saved_and_appears_in_the_ledger_of_each_account(string role)
    {
        var client = await LoginAsync(role);
        var ids = new[] { AccountId("J-RENT"), AccountId("J-UTIL"), AccountId("J-CASH") };
        var reference = NewReference();

        var post = await TestClient.PostFormAsync(client, "/Journal/New", EntryFields(reference,
            (ids[0], "800.00", ""), (ids[1], "150.00", ""), (ids[2], "", "950.00")));

        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.StartsWith("/Journal/Details/", LoginRedirect.PathOf(post));

        foreach (var id in ids)
            Assert.Contains(reference, await client.GetStringAsync($"/Ledger/Account/{id}"));
    }

    [Fact]
    public async Task Entry_with_five_lines_is_saved_and_appears_in_each_ledger()
    {
        var client = await LoginAsync("accountant");
        var ids = new[] { AccountId("J-CASH"), AccountId("J-AR"), AccountId("J-REV"), AccountId("J-AP"), AccountId("J-RENT") };
        var reference = NewReference();

        var post = await TestClient.PostFormAsync(client, "/Journal/New", EntryFields(reference,
            (ids[0], "60.00", ""), (ids[1], "40.00", ""), (ids[2], "", "70.00"),
            (ids[3], "", "20.00"), (ids[4], "", "10.00")));

        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        foreach (var id in ids)
            Assert.Contains(reference, await client.GetStringAsync($"/Ledger/Account/{id}"));
    }

    [Fact]
    public async Task Unbalanced_entry_is_not_saved_and_the_form_shows_why()
    {
        var client = await LoginAsync("accountant");
        var rent = AccountId("J-RENT");
        var cash = AccountId("J-CASH");
        var reference = NewReference();

        var post = await TestClient.PostFormAsync(client, "/Journal/New", EntryFields(reference,
            (rent, "100.00", ""), (cash, "", "99.99")));

        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        Assert.Contains("must be equal", await post.Content.ReadAsStringAsync());
        Assert.False(EntryExists(reference));
        Assert.DoesNotContain(reference, await client.GetStringAsync($"/Ledger/Account/{rent}"));
    }

    // SR-1: a deactivated account is not offered on the page and is refused if posted to anyway
    [Fact]
    public async Task An_inactive_account_is_not_offered_and_cannot_be_posted_to()
    {
        var client = await LoginAsync("accountant");
        var page = await client.GetStringAsync("/Journal/New");
        Assert.Contains("J-CASH", page);
        Assert.DoesNotContain("J-OLD", page);

        var reference = NewReference();
        var post = await TestClient.PostFormAsync(client, "/Journal/New", EntryFields(reference,
            (AccountId("J-OLD"), "10.00", ""), (AccountId("J-CASH"), "", "10.00")));

        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        Assert.Contains("inactive", await post.Content.ReadAsStringAsync());
        Assert.False(EntryExists(reference));
    }

    // d5: another company cannot see or use these accounts, nor open its entries
    [Fact]
    public async Task Another_company_cannot_be_seen_or_used()
    {
        var client = await LoginAsync("accountant");   // accountant of company A

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/Ledger/Account/{_data.AccountBId}")).StatusCode);

        var reference = NewReference();
        var post = await TestClient.PostFormAsync(client, "/Journal/New", EntryFields(reference,
            (_data.AccountBId, "10.00", ""), (AccountId("J-CASH"), "", "10.00")));
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        Assert.Contains("account not found", await post.Content.ReadAsStringAsync());
        Assert.False(EntryExists(reference));

        int bEntryId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StockiaDbContext>();
            var entry = new JournalEntry { CompanyId = _data.CompanyBId, Date = new DateTime(2026, 11, 18), Reference = "B-ONLY" };
            entry.AddLine(_data.AccountBId, 10m, 0m);
            entry.AddLine(_data.AccountBId, 0m, 10m);
            db.JournalEntries.Add(entry);
            db.SaveChanges();
            bEntryId = entry.Id;
        }
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/Journal/Details/{bEntryId}")).StatusCode);
    }

    // SR-33: a viewer reads the ledger but cannot enter data, and nothing is written
    [Fact]
    public async Task Viewer_can_read_ledgers_but_cannot_open_the_entry_form_or_save_an_entry()
    {
        var client = await LoginAsync("viewer");
        var cash = AccountId("J-CASH");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Ledger")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/Ledger/Account/{cash}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/Journal/New")).StatusCode);

        var reference = NewReference();
        var post = await TestClient.PostFormAsync(client, "/Accounts", "/Journal/New", EntryFields(reference,
            (AccountId("J-RENT"), "10.00", ""), (cash, "", "10.00")));

        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        Assert.False(EntryExists(reference));
    }

    // d5: the new pages require login (also covered automatically by ConventionTests)
    [Theory]
    [InlineData("/Journal/New")]
    [InlineData("/Journal/Details/1")]
    [InlineData("/Ledger")]
    [InlineData("/Ledger/Account/1")]
    public async Task New_pages_redirect_unknown_visitors_to_login(string url)
    {
        var response = await TestClient.Https(_factory).GetAsync(url);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Account/Login", LoginRedirect.PathOf(response));
    }
}
