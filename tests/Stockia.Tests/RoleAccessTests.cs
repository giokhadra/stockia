using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Stockia.Web.Data;
using Xunit;

namespace Stockia.Tests;

/// <summary>System test of SR-33 (d3): every role tries every page; a viewer cannot change data and
/// an accountant cannot reach Company B. The matrix failure list must stay empty.</summary>
public class RoleAccessTests : IClassFixture<StockiaFactory>
{
    private readonly StockiaFactory _factory;
    private readonly AccessSeeding.Data _data;

    public RoleAccessTests(StockiaFactory factory)
    {
        _factory = factory;
        _data = AccessSeeding.Ensure(factory, "roles");
    }

    private static readonly string[] AllRoles = { "admin", "chief", "accountant", "viewer" };

    private async Task<HttpClient> LoginAsync(string role)
    {
        var client = TestClient.Https(_factory);
        var response = await TestClient.LoginAsync(client, "roles-" + role, AccessSeeding.Password);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }

    [Fact]
    public async Task Every_role_on_every_page_matches_the_permission_matrix()
    {
        var pages = new (string Path, string[] Allowed)[]
        {
            ("/", AllRoles),
            ("/Accounts", AllRoles),
            ("/Ledger", AllRoles),
            ("/ExchangeRates", AllRoles),
            ("/Receipts", AllRoles),
            ("/Receipts/New", new[] { "admin", "chief", "accountant" }),
            ("/Journal/New", new[] { "admin", "chief", "accountant" }),
            ("/Account/ChooseCompany", AllRoles),
            ("/Company", new[] { "admin" }),
            ("/Company/Create", new[] { "admin" }),
            ("/User", new[] { "admin" })
        };

        var failures = new List<string>();
        foreach (var role in AllRoles)
        {
            var client = await LoginAsync(role);
            foreach (var (path, allowed) in pages)
            {
                var expected = allowed.Contains(role) ? HttpStatusCode.OK : HttpStatusCode.Forbidden;
                var actual = (await client.GetAsync(path)).StatusCode;
                if (actual != expected)
                    failures.Add($"{role} {path}: got {(int)actual}, expected {(int)expected}");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("chief")]
    [InlineData("accountant")]
    public async Task Writers_can_create_an_account_in_their_company(string role)
    {
        var client = await LoginAsync(role);
        var code = "W" + Guid.NewGuid().ToString("N")[..8];
        var response = await TestClient.PostFormAsync(client, "/Accounts", "/Accounts/Create",
            new Dictionary<string, string>
            {
                ["Code"] = code, ["Name"] = "Created by " + role, ["Type"] = "Asset", ["ParentId"] = ""
            });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockiaDbContext>();
        Assert.True(db.Accounts.Any(a => a.CompanyId == _data.CompanyAId && a.Code == code));
    }

    [Fact]
    public async Task Viewer_cannot_change_data_and_no_row_is_written()
    {
        var client = await LoginAsync("viewer");
        var code = "V" + Guid.NewGuid().ToString("N")[..8];
        var response = await TestClient.PostFormAsync(client, "/Accounts", "/Accounts/Create",
            new Dictionary<string, string>
            {
                ["Code"] = code, ["Name"] = "Viewer attempt", ["Type"] = "Asset", ["ParentId"] = ""
            });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockiaDbContext>();
        Assert.False(db.Accounts.Any(a => a.CompanyId == _data.CompanyAId && a.Code == code));
    }

    [Fact]
    public async Task Viewer_cannot_open_the_edit_form_or_deactivate_an_account()
    {
        var client = await LoginAsync("viewer");

        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.GetAsync($"/Accounts/Edit/{_data.AccountAId}")).StatusCode);

        var response = await TestClient.PostFormAsync(client, "/Accounts", "/Accounts/SetActive",
            new Dictionary<string, string>
            {
                ["id"] = _data.AccountAId.ToString(), ["active"] = "false"
            });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("chief")]
    [InlineData("accountant")]
    [InlineData("viewer")]
    public async Task Nobody_can_choose_a_company_they_are_not_assigned_to(string role)
    {
        var client = await LoginAsync(role);
        var response = await TestClient.PostFormAsync(client, "/Account/ChooseCompany",
            new Dictionary<string, string> { ["CompanyId"] = _data.CompanyBId.ToString() });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Accountant_sees_only_own_company_and_cannot_open_another_company_account()
    {
        var client = await LoginAsync("accountant");

        var page = await client.GetStringAsync("/Accounts");
        Assert.Contains("A cash", page);
        Assert.DoesNotContain("B only", page);
        Assert.DoesNotContain("C only", page);

        Assert.Equal(HttpStatusCode.NotFound,
            (await client.GetAsync($"/Accounts/Edit/{_data.AccountBId}")).StatusCode);
    }

    [Theory]
    [InlineData("chief", "/Company/Create")]
    [InlineData("accountant", "/Company/Create")]
    [InlineData("viewer", "/Company/Create")]
    [InlineData("chief", "/User/Assign")]
    [InlineData("accountant", "/User/Assign")]
    [InlineData("viewer", "/User/Assign")]
    public async Task Non_admins_cannot_write_admin_pages(string role, string path)
    {
        var client = await LoginAsync(role);
        var response = await TestClient.PostFormAsync(client, "/", path,
            new Dictionary<string, string>
            {
                ["Name"] = "Hack Co",
                ["TaxNumber"] = "",
                ["BaseCurrency"] = "USD",
                ["UserId"] = _data.ViewerUserId.ToString(),
                ["CompanyId"] = _data.CompanyAId.ToString(),
                ["RoleId"] = _data.ViewerRoleId.ToString()
            });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
