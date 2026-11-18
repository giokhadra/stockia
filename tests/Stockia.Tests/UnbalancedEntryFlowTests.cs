using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stockia.Web.Data;
using Stockia.Web.Domain;
using Xunit;

namespace Stockia.Tests;

/// <summary>SR-3 / d3 system test: posting an unbalanced entry is blocked and a message shows the difference.
/// Also the d4 check: every entry that gets posted is balanced.</summary>
public class UnbalancedEntryFlowTests : IClassFixture<StockiaFactory>
{
    private readonly StockiaFactory _factory;
    private readonly AccessSeeding.Data _data;

    public UnbalancedEntryFlowTests(StockiaFactory factory)
    {
        _factory = factory;
        _data = AccessSeeding.Ensure(factory, "unbalanced");
        EnsureAccounts();
    }

    private void EnsureAccounts()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockiaDbContext>();
        foreach (var (code, name) in new[] { ("U-RENT", "Unbalanced rent"), ("U-UTIL", "Unbalanced utilities"), ("U-CASH", "Unbalanced cash") })
            if (!db.Accounts.Any(a => a.CompanyId == _data.CompanyAId && a.Code == code))
                db.Accounts.Add(new Account { CompanyId = _data.CompanyAId, Code = code, Name = name, Type = "Asset" });
        db.SaveChanges();
    }

    private int AccountId(string code)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockiaDbContext>();
        return db.Accounts.Single(a => a.CompanyId == _data.CompanyAId && a.Code == code).Id;
    }

    private async Task<HttpClient> LoginAsync()
    {
        var client = TestClient.Https(_factory);
        var response = await TestClient.LoginAsync(client, "unbalanced-accountant", AccessSeeding.Password);
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
            ["Description"] = "SR-3 system test"
        };
        for (var i = 0; i < lines.Length; i++)
        {
            fields[$"Lines[{i}].AccountId"] = lines[i].account.ToString();
            fields[$"Lines[{i}].Debit"] = lines[i].debit;
            fields[$"Lines[{i}].Credit"] = lines[i].credit;
        }
        return fields;
    }

    private static string NewReference(string prefix = "JE-") => prefix + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task Unbalanced_entry_is_blocked_and_a_message_shows_the_difference()
    {
        var client = await LoginAsync();
        var ids = new[] { AccountId("U-RENT"), AccountId("U-UTIL"), AccountId("U-CASH") };
        var reference = NewReference();

        var post = await TestClient.PostFormAsync(client, "/Journal/New", EntryFields(reference,
            (ids[0], "800.00", ""), (ids[1], "150.00", ""), (ids[2], "", "900.00")));

        Assert.Equal(HttpStatusCode.OK, post.StatusCode);           // stays on the form, no redirect to Details
        Assert.Contains("The difference is 50.00", await post.Content.ReadAsStringAsync());

        foreach (var id in ids)                                      // nothing reached any ledger
            Assert.DoesNotContain(reference, await client.GetStringAsync($"/Ledger/Account/{id}"));
    }

    [Fact]
    public async Task Difference_is_shown_when_credits_are_higher_too()
    {
        var client = await LoginAsync();

        var post = await TestClient.PostFormAsync(client, "/Journal/New", EntryFields(NewReference(),
            (AccountId("U-RENT"), "100.00", ""), (AccountId("U-CASH"), "", "150.50")));

        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        Assert.Contains("The difference is 50.50", await post.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_form_keeps_what_the_user_typed_after_the_refusal()
    {
        var client = await LoginAsync();
        var reference = NewReference();

        var post = await TestClient.PostFormAsync(client, "/Journal/New", EntryFields(reference,
            (AccountId("U-RENT"), "100.00", ""), (AccountId("U-CASH"), "", "99.99")));

        var html = await post.Content.ReadAsStringAsync();
        Assert.Contains($"value=\"{reference}\"", html);
        Assert.Contains("value=\"100.00\"", html);
        Assert.Contains("value=\"99.99\"", html);
    }

    // d4 / BR-2: after good and bad attempts, every entry that was saved is balanced
    [Fact]
    public async Task Every_saved_entry_is_balanced()
    {
        var client = await LoginAsync();
        var rent = AccountId("U-RENT");
        var cash = AccountId("U-CASH");

        var bad = await TestClient.PostFormAsync(client, "/Journal/New", EntryFields(NewReference("BAD-"),
            (rent, "100.00", ""), (cash, "", "99.99")));
        Assert.Equal(HttpStatusCode.OK, bad.StatusCode);

        var good = await TestClient.PostFormAsync(client, "/Journal/New", EntryFields(NewReference("OK-"),
            (rent, "100.00", ""), (cash, "", "100.00")));
        Assert.Equal(HttpStatusCode.Redirect, good.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockiaDbContext>();
        var entries = await db.JournalEntries.Include(e => e.Lines).ToListAsync();
        Assert.NotEmpty(entries);
        Assert.All(entries, e => Assert.True(e.IsBalanced, $"Entry {e.Reference} is not balanced"));
    }
}
