using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stockia.Web.Data;
using Xunit;

namespace Stockia.Tests;

public class StockiaFactory : WebApplicationFactory<Program>
{
    private readonly string _dbName = Guid.NewGuid().ToString();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            var old = services.Single(d => d.ServiceType == typeof(DbContextOptions<StockiaDbContext>));
            services.Remove(old);
            services.AddDbContext<StockiaDbContext>(o => o.UseInMemoryDatabase(_dbName));
        });
    }
}

public class AuthFlowTests : IClassFixture<StockiaFactory>
{
    private readonly StockiaFactory _factory;
    public AuthFlowTests(StockiaFactory factory) => _factory = factory;

    private HttpClient NewClient() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true
    });

    private static async Task<HttpResponseMessage> PostLogin(HttpClient c, string user, string pass)
    {
        var html = await c.GetStringAsync("/Account/Login");
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        return await c.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Username"] = user, ["Password"] = pass, ["__RequestVerificationToken"] = token
        }));
    }

    [Fact]
    public async Task Unknown_visitor_is_redirected_to_login()
    {
        var r = await NewClient().GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        Assert.Equal("/Account/Login", LoginRedirect.PathOf(r));
    }

    [Fact]
    public async Task Seeded_user_logs_in_and_sees_home()
    {
        var c = NewClient();
        var login = await PostLogin(c, DbSeeder.SeedUsername, DbSeeder.SeedPassword);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        var home = await c.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        Assert.Contains("Welcome, Administrator", await home.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Wrong_password_stays_on_login_and_home_stays_locked()
    {
        var c = NewClient();
        var login = await PostLogin(c, DbSeeder.SeedUsername, "wrong");
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await c.GetAsync("/")).StatusCode);
    }
}

/// <summary>Redirect Location can be relative or absolute; compare only its path.</summary>
public static class LoginRedirect
{
    public static string PathOf(HttpResponseMessage r) =>
        new Uri(new Uri("http://localhost"), r.Headers.Location!).AbsolutePath;
}
