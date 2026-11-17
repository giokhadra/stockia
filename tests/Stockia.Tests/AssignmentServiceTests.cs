using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Domain;
using Stockia.Web.Services;
using Xunit;

namespace Stockia.Tests;

public class AssignmentServiceTests
{
    private static async Task<(AssignmentService Service, StockiaDbContext Db)> Build()
    {
        var options = new DbContextOptionsBuilder<StockiaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var db = new StockiaDbContext(options);

        var a = new Company { Name = "Company A" };
        var b = new Company { Name = "Company B" };
        db.Companies.AddRange(a, b);
        await db.SaveChangesAsync();

        db.AppUsers.Add(new AppUser { CompanyId = a.Id, Username = "rana", PasswordHash = "x", DisplayName = "Rana Haddad" });
        db.Roles.AddRange(new Role { Name = "Administrator" }, new Role { Name = "Viewer" });
        await db.SaveChangesAsync();
        return (new AssignmentService(db), db);
    }

    [Fact]
    public async Task Assigns_a_user_to_a_company_with_a_role()
    {
        var (service, db) = await Build();
        var user = await db.AppUsers.SingleAsync();
        var company = await db.Companies.SingleAsync(c => c.Name == "Company A");
        var role = await db.Roles.SingleAsync(r => r.Name == "Administrator");

        var (assignment, error) = await service.AssignAsync(user.Id, company.Id, role.Id);

        Assert.Null(error);
        Assert.NotNull(assignment);
        Assert.Equal(role.Id, (await db.Assignments.SingleAsync()).RoleId);
    }

    [Fact]
    public async Task Assigning_again_changes_the_role_instead_of_adding_a_second_row()
    {
        var (service, db) = await Build();
        var user = await db.AppUsers.SingleAsync();
        var company = await db.Companies.SingleAsync(c => c.Name == "Company A");
        var admin = await db.Roles.SingleAsync(r => r.Name == "Administrator");
        var viewer = await db.Roles.SingleAsync(r => r.Name == "Viewer");

        await service.AssignAsync(user.Id, company.Id, admin.Id);
        await service.AssignAsync(user.Id, company.Id, viewer.Id);

        var saved = await db.Assignments.SingleAsync();
        Assert.Equal(viewer.Id, saved.RoleId);
    }

    [Fact]
    public async Task Refuses_an_unknown_user_company_or_role()
    {
        var (service, db) = await Build();
        var user = await db.AppUsers.SingleAsync();
        var company = await db.Companies.FirstAsync();
        var role = await db.Roles.FirstAsync();

        Assert.NotNull((await service.AssignAsync(999, company.Id, role.Id)).Error);
        Assert.NotNull((await service.AssignAsync(user.Id, 999, role.Id)).Error);
        Assert.NotNull((await service.AssignAsync(user.Id, company.Id, 999)).Error);
        Assert.Empty(db.Assignments);
    }

    // BRD SR-20: a user sees only assigned companies
    [Fact]
    public async Task A_user_sees_only_the_companies_assigned_to_them()
    {
        var (service, db) = await Build();
        var user = await db.AppUsers.SingleAsync();
        var a = await db.Companies.SingleAsync(c => c.Name == "Company A");
        var role = await db.Roles.FirstAsync();

        await service.AssignAsync(user.Id, a.Id, role.Id);

        var companies = await service.CompaniesOfAsync(user.Id);
        Assert.Equal(new[] { "Company A" }, companies.Select(c => c.Name));
    }
}