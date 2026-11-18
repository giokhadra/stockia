using System.Net;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Stockia.Web.Controllers;
using Stockia.Web.Data;
using Stockia.Web.Domain;
using Xunit;

namespace Stockia.Tests;

/// <summary>System test of SR-6 (d3), through the pages: an entry shows who posted it and when, a mistake is
/// corrected by a reversal (never a deletion), and a viewer reads but never writes. Also d5: company isolation.</summary>
public class JournalAuditFlowTests : IClassFixture<StockiaFactory>
{
    private readonly StockiaFactory _factory;
    private readonly AccessSeeding.Data _data;

    public JournalAuditFlowTests(StockiaFactory factory)
    {
        _factory = factory;
        _data = AccessSeeding.Ensure(factory, "audit");
        EnsureAccounts();
    }

    private void EnsureAccounts()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockiaDbContext>();
        foreach (var (code, name) in new[] { ("AU-CASH", "Audit cash"), ("AU-REV", "Audit revenue") })
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

    private async Task<HttpClient> LoginAsync(string role)
    {
        var client = TestClient.Https(_factory);
        var response = await TestClient.LoginAsync(client, "audit-" + role, AccessSeeding.Password);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }

    private static string NewReference() => "AU-" + Guid.NewGuid().ToString("N")[..8];

    private Dictionary<string, string> EntryFields(string reference) => new()
    {
        ["Date"] = "2026-11-18", ["Reference"] = reference, ["Description"] = "Audit system test",
        ["Lines[0].AccountId"] = AccountId("AU-CASH").ToString(), ["Lines[0].Debit"] = "100.00", ["Lines[0].Credit"] = "",
        ["Lines[1].AccountId"] = AccountId("AU-REV").ToString(), ["Lines[1].Debit"] = "", ["Lines[1].Credit"] = "100.00"
    };

    /// <summary>Posts an entry through the page as the given role; returns the path of its page and its id.</summary>
    private async Task<(string Path, int Id)> PostEntryAsync(HttpClient client, string reference)
    {
        var post = await TestClient.PostFormAsync(client, "/Journal/New", EntryFields(reference));
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        var path = LoginRedirect.PathOf(post);
        Assert.StartsWith("/Journal/Details/", path);
        return (path, int.Parse(path.Split('/').Last()));
    }

    private int SeedEntry(int companyId, string reference, int accountA, int accountB)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockiaDbContext>();
        var entry = new JournalEntry { CompanyId = companyId, Date = new DateTime(2026, 11, 18), Reference = reference };
        entry.AddLine(accountA, 10m, 0m);
        entry.AddLine(accountB, 0m, 10m);
        db.JournalEntries.Add(entry);
        db.SaveChanges();
        return entry.Id;
    }

    [Fact]
    public async Task A_posted_entry_shows_who_posted_it_and_when()
    {
        var client = await LoginAsync("accountant");
        var reference = NewReference();
        var (path, id) = await PostEntryAsync(client, reference);

        var page = await client.GetStringAsync(path);
        Assert.Contains("History", page);
        Assert.Contains("entry posted", page);
        Assert.Contains("Access Accountant", page);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockiaDbContext>();
        var log = db.AuditLogs.Single(l => l.EntryId == id);
        Assert.Equal(_data.AccountantUserId, log.UserId);
        Assert.Equal(_data.CompanyAId, log.CompanyId);
        Assert.Equal(reference, log.Details);
    }

    // BRD SR-6: corrected by reversal, not deletion
    [Fact]
    public async Task A_mistake_is_corrected_by_a_reversal_and_both_entries_stay_in_the_ledger()
    {
        var client = await LoginAsync("accountant");
        var reference = NewReference();
        var (path, id) = await PostEntryAsync(client, reference);

        var reverse = await TestClient.PostFormAsync(client, path, $"/Journal/Reverse/{id}",
            new Dictionary<string, string>());
        Assert.Equal(HttpStatusCode.Redirect, reverse.StatusCode);
        var reversalPath = LoginRedirect.PathOf(reverse);
        Assert.StartsWith("/Journal/Details/", reversalPath);

        var reversalPage = await client.GetStringAsync(reversalPath);
        Assert.Contains("Reversal of", reversalPage);
        Assert.Contains("REV-" + reference, reversalPage);
        Assert.Contains("reversal posted", reversalPage);

        var originalPage = await client.GetStringAsync(path);
        Assert.Contains("Reversed by", originalPage);
        Assert.Contains("entry reversed", originalPage);
        Assert.DoesNotContain("Reverse this entry", originalPage);   // already reversed: no second button

        var ledger = await client.GetStringAsync($"/Ledger/Account/{AccountId("AU-CASH")}");
        Assert.Contains(reference, ledger);
        Assert.Contains("REV-" + reference, ledger);
    }

    [Fact]
    public async Task An_entry_cannot_be_reversed_twice_through_the_page()
    {
        var client = await LoginAsync("accountant");
        var (path, id) = await PostEntryAsync(client, NewReference());
        await TestClient.PostFormAsync(client, path, $"/Journal/Reverse/{id}", new Dictionary<string, string>());

        // the entry page has no form any more, so take the antiforgery token from another page
        var again = await TestClient.PostFormAsync(client, "/Accounts", $"/Journal/Reverse/{id}",
            new Dictionary<string, string>());

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Contains("already been reversed", await again.Content.ReadAsStringAsync());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockiaDbContext>();
        Assert.Equal(1, db.JournalEntries.Count(e => e.ReversesEntryId == id));
    }

    // SR-33: a viewer reads the history but cannot reverse
    [Fact]
    public async Task Viewer_can_read_the_history_and_the_audit_log_but_cannot_reverse()
    {
        var accountant = await LoginAsync("accountant");
        var reference = NewReference();
        var (path, id) = await PostEntryAsync(accountant, reference);

        var viewer = await LoginAsync("viewer");
        Assert.Contains("entry posted", await viewer.GetStringAsync(path));

        var audit = await viewer.GetStringAsync("/Audit");
        Assert.Contains("entry posted", audit);
        Assert.Contains(reference, audit);

        var reverse = await TestClient.PostFormAsync(viewer, "/Accounts", $"/Journal/Reverse/{id}",
            new Dictionary<string, string>());
        Assert.Equal(HttpStatusCode.Forbidden, reverse.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockiaDbContext>();
        Assert.False(db.JournalEntries.Any(e => e.ReversesEntryId == id));
    }

    // BRD SR-6: posted entries have no delete option
    [Fact]
    public async Task There_is_no_delete_option_for_a_posted_entry()
    {
        var deleteActions = typeof(JournalController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.Name.Contains("Delete", StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Name)
            .ToList();
        Assert.Empty(deleteActions);

        var client = await LoginAsync("admin");
        var (path, _) = await PostEntryAsync(client, NewReference());
        Assert.DoesNotContain("delete", (await client.GetStringAsync(path)), StringComparison.OrdinalIgnoreCase);
    }

    // d5: another company's entries and audit lines are invisible
    [Fact]
    public async Task Another_companys_entry_cannot_be_reversed_and_its_audit_lines_are_not_shown()
    {
        var entryOfB = SeedEntry(_data.CompanyBId, "B-AUDIT-ONLY", _data.AccountBId, _data.AccountBId);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StockiaDbContext>();
            db.AuditLogs.Add(new AuditLog
            {
                CompanyId = _data.CompanyBId, UserId = _data.AdminUserId, EntryId = entryOfB,
                LogTime = DateTime.UtcNow, Action = "entry posted", Details = "B-secret-detail"
            });
            db.SaveChanges();
        }

        var client = await LoginAsync("accountant");   // accountant of company A

        Assert.DoesNotContain("B-secret-detail", await client.GetStringAsync("/Audit"));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/Journal/Details/{entryOfB}")).StatusCode);

        var reverse = await TestClient.PostFormAsync(client, "/Accounts", $"/Journal/Reverse/{entryOfB}",
            new Dictionary<string, string>());
        Assert.Equal(HttpStatusCode.NotFound, reverse.StatusCode);

        using var check = _factory.Services.CreateScope();
        var checkDb = check.ServiceProvider.GetRequiredService<StockiaDbContext>();
        Assert.False(checkDb.JournalEntries.Any(e => e.ReversesEntryId == entryOfB));
    }
}