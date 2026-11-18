using System.Net;
using System.Security.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Stockia.Web.Services;
using Xunit;

namespace Stockia.Tests;

/// <summary>The app as deployed: an HTTPS port is known, so http requests can be redirected.</summary>
public class HttpsPortFactory : StockiaFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseSetting("https_port", "443");
    }
}

/// <summary>The app outside Development, where HSTS is switched on.</summary>
public class ProductionFactory : StockiaFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseEnvironment("Production");
    }
}

/// <summary>Item 4 (SR-34), task 4.2, integration and system tests: all traffic over HTTPS.</summary>
public class HttpsRedirectTests : IClassFixture<HttpsPortFactory>
{
    private readonly HttpsPortFactory _factory;
    public HttpsRedirectTests(HttpsPortFactory factory) => _factory = factory;

    [Fact]
    public async Task An_http_request_is_redirected_to_https()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost"),
            AllowAutoRedirect = false
        });

        var r = await client.GetAsync("/Account/Login");

        Assert.True(r.StatusCode is HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect,
            $"expected a redirect, got {(int)r.StatusCode}");
        Assert.Equal("https", r.Headers.Location!.Scheme);
        Assert.Equal("/Account/Login", r.Headers.Location.AbsolutePath);
    }

    [Fact]
    public async Task An_https_request_is_served()
    {
        var r = await TestClient.Https(_factory).GetAsync("/Account/Login");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
    }
}

public class HstsTests : IClassFixture<ProductionFactory>
{
    private readonly ProductionFactory _factory;
    public HstsTests(ProductionFactory factory) => _factory = factory;

    [Fact]
    public async Task Https_responses_tell_the_browser_to_use_https_only_for_a_year()
    {
        // HSTS is never sent for localhost, so the request uses a real-looking host name.
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://stockia.example"),
            AllowAutoRedirect = false
        });

        var r = await client.GetAsync("/Account/Login");

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var hsts = string.Join(";", r.Headers.GetValues("Strict-Transport-Security"));
        Assert.Contains("max-age=31536000", hsts);
        Assert.Contains("includeSubDomains", hsts);
    }
}

public class SecureCookieTests : IClassFixture<StockiaFactory>
{
    private readonly StockiaFactory _factory;
    public SecureCookieTests(StockiaFactory factory) => _factory = factory;

    private static string CookieNamed(HttpResponseMessage r, string prefix) =>
        r.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith(prefix, StringComparison.Ordinal));

    [Fact]
    public async Task The_sign_in_and_form_cookies_are_https_only_and_hidden_from_page_scripts()
    {
        var client = TestClient.Https(_factory);

        var page = await client.GetAsync("/Account/Login");
        var formCookie = CookieNamed(page, ".AspNetCore.Antiforgery");
        Assert.Contains("secure", formCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", formCookie, StringComparison.OrdinalIgnoreCase);

        var login = await TestClient.LoginAsync(client);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var signIn = CookieNamed(login, ".AspNetCore.Cookies");
        Assert.Contains("secure", signIn, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", signIn, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Task 4.2: TLS 1.2 or later only.</summary>
public class TlsPolicyTests
{
    [Fact]
    public void Only_TLS_1_2_or_later_is_allowed()
    {
        var allowed = TlsPolicy.Allowed;

        Assert.True(allowed.HasFlag(SslProtocols.Tls12));
        Assert.Equal(SslProtocols.None, allowed & ~(SslProtocols.Tls12 | SslProtocols.Tls13));
    }
}
