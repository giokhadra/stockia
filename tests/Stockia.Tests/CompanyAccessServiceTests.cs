using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Domain;
using Stockia.Web.Security;
using Xunit;

namespace Stockia.Tests;

/// <summary>Unit tests of the access-filter decisions (SR-33), written before the filter.
/// The filter itself is a thin adapter around CompanyAccessService.</summary>
public class CompanyAccessServiceTests
{
    private sealed record Fixture(
        CompanyAccessService Access, int AccountantId, int ViewerId, int CompanyAId, int CompanyBId);

    private static async Task<Fixture> BuildAsync()
    {
        var options = new DbContextOptionsBuilder<StockiaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var db = new StockiaDbContext(options);

        var a = new Company { Name = "Company A" };
        var b = new Company { Name = "Company B" };
        db.Companies.AddRange(a, b);
        db.Roles.AddRange(
            new Role { Name = CompanyRoles.Administrator },
            new Role { Name = CompanyRoles.ChiefAccountant },
            new Role { Name = CompanyRoles.Accountant },
            new Role { Name = CompanyRoles.Viewer });
        await db.SaveChangesAsync();

        var accountant = new AppUser { CompanyId = a.Id, Username = "acc", PasswordHash = "x", DisplayName = "Accountant" };
        var viewer = new AppUser { CompanyId = a.Id, Username = "view", PasswordHash = "x", DisplayName = "Viewer" };
        db.AppUsers.AddRange(accountant, viewer);
        await db.SaveChangesAsync();

        db.Assignments.AddRange(
            new Assignment { UserId = accountant.Id, CompanyId = a.Id, RoleId = RoleId(db, CompanyRoles.Accountant) },
            new Assignment { UserId = viewer.Id, CompanyId = a.Id, RoleId = RoleId(db, CompanyRoles.Viewer) });
        await db.SaveChangesAsync();

        return new Fixture(new CompanyAccessService(db), accountant.Id, viewer.Id, a.Id, b.Id);
    }

    private static int RoleId(StockiaDbContext db, string name) => db.Roles.Single(r => r.Name == name).Id;

    // d2: the access filter refuses a user without the needed role.
    [Fact]
    public async Task Refuses_a_user_without_the_needed_role()
    {
        var f = await BuildAsync();

        var result = await f.Access.CheckAsync(f.ViewerId, f.CompanyAId,
            new[] { CompanyRoles.Administrator, CompanyRoles.ChiefAccountant, CompanyRoles.Accountant });

        Assert.Equal(CompanyAccessResult.Forbidden, result);
    }

    // d2: the access filter refuses a user of another company.
    [Fact]
    public async Task Refuses_a_user_of_another_company()
    {
        var f = await BuildAsync();

        var result = await f.Access.CheckAsync(f.AccountantId, f.CompanyBId, Array.Empty<string>());

        Assert.Equal(CompanyAccessResult.Forbidden, result);
    }

    [Fact]
    public async Task Allows_an_assigned_user_who_holds_the_needed_role()
    {
        var f = await BuildAsync();

        var result = await f.Access.CheckAsync(f.AccountantId, f.CompanyAId,
            new[] { CompanyRoles.Accountant, CompanyRoles.ChiefAccountant });

        Assert.Equal(CompanyAccessResult.Allowed, result);
    }

    [Fact]
    public async Task Allows_a_viewer_to_read_a_page_that_needs_no_particular_role()
    {
        var f = await BuildAsync();

        Assert.Equal(CompanyAccessResult.Allowed,
            await f.Access.CheckAsync(f.ViewerId, f.CompanyAId, Array.Empty<string>()));
    }

    [Fact]
    public async Task Reports_a_missing_company_before_it_looks_at_roles()
    {
        var f = await BuildAsync();

        Assert.Equal(CompanyAccessResult.NoCompanyChosen,
            await f.Access.CheckAsync(f.ViewerId, null, new[] { CompanyRoles.Administrator }));
    }
}
