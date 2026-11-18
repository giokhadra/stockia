using System.Net;
using Xunit;

namespace Stockia.Tests;

/// <summary>System test of SR-33: a user with several companies chooses one after login, the choice
/// sets the company context, and the role is the one held in that chosen company.</summary>
public class CompanyChoiceTests : IClassFixture<StockiaFactory>
{
    private readonly StockiaFactory _factory;
    private readonly AccessSeeding.Data _data;

    public CompanyChoiceTests(StockiaFactory factory)
    {
        _factory = factory;
        _data = AccessSeeding.Ensure(factory, "choice");
    }

    private async Task<HttpClient> LoginMultiAsync()
    {
        var client = TestClient.Https(_factory);
        var login = await TestClient.LoginAsync(client, "choice-multi", AccessSeeding.Password);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        return client;
    }

    [Fact]
    public async Task A_user_with_two_companies_chooses_one_after_login()
    {
        var client = TestClient.Https(_factory);
        var login = await TestClient.LoginAsync(client, "choice-multi", AccessSeeding.Password);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/Account/ChooseCompany", LoginRedirect.PathOf(login));

        var blocked = await client.GetAsync("/Accounts");
        Assert.Equal(HttpStatusCode.Redirect, blocked.StatusCode);
        Assert.Equal("/Account/ChooseCompany", LoginRedirect.PathOf(blocked));

        var page = await client.GetStringAsync("/Account/ChooseCompany");
        Assert.Contains("choice A", page);
        Assert.Contains("choice B", page);

        var choose = await TestClient.PostFormAsync(client, "/Account/ChooseCompany",
            new Dictionary<string, string> { ["CompanyId"] = _data.CompanyBId.ToString() });
        Assert.Equal(HttpStatusCode.Redirect, choose.StatusCode);

        var accounts = await client.GetStringAsync("/Accounts");
        Assert.Contains("B only", accounts);
        Assert.DoesNotContain("A cash", accounts);
    }

    [Fact]
    public async Task The_role_comes_from_the_chosen_company_not_from_the_user()
    {
        var client = await LoginMultiAsync();
        await TestClient.PostFormAsync(client, "/Account/ChooseCompany",
            new Dictionary<string, string> { ["CompanyId"] = _data.CompanyBId.ToString() });

        var write = await TestClient.PostFormAsync(client, "/Accounts", "/Accounts/Create",
            new Dictionary<string, string>
            {
                ["Code"] = "CHG" + Guid.NewGuid().ToString("N")[..6],
                ["Name"] = "Viewer in B",
                ["Type"] = "Asset",
                ["ParentId"] = ""
            });

        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    [Fact]
    public async Task Choosing_an_unassigned_company_is_refused()
    {
        var client = await LoginMultiAsync();
        var response = await TestClient.PostFormAsync(client, "/Account/ChooseCompany",
            new Dictionary<string, string> { ["CompanyId"] = _data.CompanyCId.ToString() });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
