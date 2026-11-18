using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Stockia.Web.Data;

namespace Stockia.Tests;

/// <summary>Helpers for page tests: an HTTPS browser-like client, form posts with the antiforgery token, and login.</summary>
public static class TestClient
{
    /// <summary>Cookies are HTTPS-only (SR-34), so page tests talk to the app over https.</summary>
    public static HttpClient Https(WebApplicationFactory<Program> factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = true
        });

    /// <summary>Opens the page at <paramref name="path"/>, then posts its form with the antiforgery token.</summary>
    public static Task<HttpResponseMessage> PostFormAsync(HttpClient client, string path, Dictionary<string, string> fields) =>
        PostFormAsync(client, path, path, fields);

    /// <summary>Opens <paramref name="tokenPage"/> for the antiforgery token, then posts it to
    /// <paramref name="postPath"/> (a page whose form targets a different action).</summary>
    public static async Task<HttpResponseMessage> PostFormAsync(
        HttpClient client, string tokenPage, string postPath, Dictionary<string, string> fields)
    {
        var html = await client.GetStringAsync(tokenPage);
        fields["__RequestVerificationToken"] =
            Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value;
        return await client.PostAsync(postPath, new FormUrlEncodedContent(fields));
    }

    public static Task<HttpResponseMessage> LoginAsync(HttpClient client,
        string username = DbSeeder.SeedUsername, string password = DbSeeder.SeedPassword) =>
        PostFormAsync(client, "/Account/Login", new Dictionary<string, string>
        {
            ["Username"] = username,
            ["Password"] = password
        });
}
