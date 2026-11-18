using System.Security.Claims;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using Stockia.Web.Data;
using Stockia.Web.Domain;
using Stockia.Web.Security;
using Stockia.Web.Services;
using Xunit;

namespace Stockia.Tests;

/// <summary>Definition of Done checks that guard every future page and table.</summary>
public class ConventionTests : IClassFixture<StockiaFactory>
{
    private readonly StockiaFactory _factory;
    public ConventionTests(StockiaFactory factory) => _factory = factory;

    // 1.3 + d5: every page requires login. New controllers are covered automatically.
    [Fact]
    public async Task Every_non_anonymous_page_redirects_unknown_visitors_to_login()
    {
        var endpoints = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<ControllerActionDescriptor>() is not null)
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is null)
            .Where(e => e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains("GET") ?? true)
            .ToList();
        Assert.NotEmpty(endpoints);

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),   // SR-34: the app is used over https
            AllowAutoRedirect = false
        });
        foreach (var e in endpoints)
        {
            var cad = e.Metadata.GetMetadata<ControllerActionDescriptor>()!;
            var path = cad.AttributeRouteInfo is null
                ? $"/{cad.ControllerName}/{cad.ActionName}"
                : "/" + e.RoutePattern.RawText?.TrimStart('/');
            if (path.Contains('{')) continue; // route needs parameters: add a dedicated test for it

            var r = await client.GetAsync(path);
            Assert.True(r.StatusCode == HttpStatusCode.Redirect &&
                        LoginRedirect.PathOf(r) == "/Account/Login",
                        $"{path} is reachable without login (status {(int)r.StatusCode})");
        }
    }

    // d5: every write action names the roles allowed to call it, so a viewer never writes by accident.
    [Fact]
    public void Every_write_action_declares_the_company_roles_it_requires()
    {
        var actions = _factory.Services.GetRequiredService<IActionDescriptorCollectionProvider>()
            .ActionDescriptors.Items.OfType<ControllerActionDescriptor>();

        var missing = new List<string>();
        foreach (var action in actions)
        {
            if (!IsWrite(action)) continue;
            if (Has<AllowAnonymousAttribute>(action) || Has<AllowNoCompanyAttribute>(action)) continue;

            var roles = action.MethodInfo.GetCustomAttributes<RequireCompanyRoleAttribute>(true)
                .Concat(action.ControllerTypeInfo.GetCustomAttributes<RequireCompanyRoleAttribute>(true));
            if (!roles.Any()) missing.Add($"{action.ControllerName}.{action.ActionName}");
        }

        Assert.True(missing.Count == 0,
            "Write actions without [RequireCompanyRole]: " + string.Join(", ", missing));
    }

    private static bool IsWrite(ControllerActionDescriptor action) =>
        action.MethodInfo.GetCustomAttributes<HttpPostAttribute>(true).Any()
        || action.MethodInfo.GetCustomAttributes<HttpPutAttribute>(true).Any()
        || action.MethodInfo.GetCustomAttributes<HttpPatchAttribute>(true).Any()
        || action.MethodInfo.GetCustomAttributes<HttpDeleteAttribute>(true).Any();

    private static bool Has<T>(ControllerActionDescriptor action) where T : Attribute =>
        action.MethodInfo.GetCustomAttributes<T>(true).Any()
        || action.ControllerTypeInfo.GetCustomAttributes<T>(true).Any();

    // d5: users carry a company, and every business table must have CompanyId.
    [Fact]
    public void Principal_exposes_the_company_of_the_signed_in_user()
    {
        var p = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimsExtensions.CompanyIdClaim, "7") }, "test"));
        Assert.Equal(7, p.GetCompanyId());
        Assert.Null(new ClaimsPrincipal(new ClaimsIdentity()).GetCompanyId());
    }

    [Fact]
    public void Every_entity_except_Company_has_a_CompanyId()
    {
        using var scope = _factory.Services.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<StockiaDbContext>().Model;
        var missing = model.GetEntityTypes()
            .Where(t => t.ClrType != typeof(Company) && t.ClrType != typeof(Role))
            .Where(t => t.FindProperty("CompanyId") is null)
            .Select(t => t.ClrType.Name).ToList();
        Assert.True(missing.Count == 0, "Missing CompanyId: " + string.Join(", ", missing));
    }

    // d6: money is DECIMAL(18,2) in the model; no float/double amounts.
    [Fact]
    public void Every_decimal_property_is_precision_18_scale_2_and_no_floating_point_columns()
    {
        using var scope = _factory.Services.CreateScope();
        var model = scope.ServiceProvider.GetRequiredService<StockiaDbContext>().Model;
        foreach (var prop in model.GetEntityTypes().SelectMany(t => t.GetProperties()))
        {
            var type = Nullable.GetUnderlyingType(prop.ClrType) ?? prop.ClrType;
            Assert.NotEqual(typeof(double), type);
            Assert.NotEqual(typeof(float), type);
            if (type == typeof(decimal))
            {
                Assert.Equal(18, prop.GetPrecision());
                Assert.Equal(2, prop.GetScale());
            }
        }
    }
}

public class MoneyTests
{
    [Fact]
    public void Total_equals_the_sum_of_the_displayed_rounded_lines()
    {
        var lines = new[] { 0.105m, 0.105m, 0.105m };            // each shows as 0.11
        Assert.Equal(0.33m, Money.Total(lines));                  // not 0.32 (0.315 rounded)
        Assert.Equal(lines.Select(Money.Round).Sum(), Money.Total(lines));
    }

    [Fact]
    public void Decimal_arithmetic_has_no_binary_float_error()
    {
        Assert.Equal(0.30m, Money.Total(new[] { 0.10m, 0.20m }));
    }
}
