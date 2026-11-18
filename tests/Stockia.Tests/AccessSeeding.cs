using Microsoft.Extensions.DependencyInjection;
using Stockia.Web.Data;
using Stockia.Web.Domain;
using Stockia.Web.Security;
using Stockia.Web.Services;

namespace Stockia.Tests;

/// <summary>SR-33 test data: one company per role in Company A, two more companies, one user per role,
/// and a user who is Accountant in A but Viewer in B.</summary>
public static class AccessSeeding
{
    public const string Password = "Access#2026";

    public sealed record Data(
        int CompanyAId, int CompanyBId, int CompanyCId,
        int AdminUserId, int ChiefUserId, int AccountantUserId, int ViewerUserId, int MultiUserId,
        int AccountAId, int AccountBId, int ViewerRoleId);

    public static Data Ensure(StockiaFactory factory, string prefix)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockiaDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        if (!db.Companies.Any(c => c.Name == prefix + " A"))
            Seed(db, hasher, prefix);

        return Read(db, prefix);
    }

    private static void Seed(StockiaDbContext db, IPasswordHasher hasher, string prefix)
    {
        foreach (var name in CompanyRoles.Readers)
            if (!db.Roles.Any(r => r.Name == name))
                db.Roles.Add(new Role { Name = name });
        db.SaveChanges();

        var a = new Company { Name = prefix + " A", BaseCurrency = "USD" };
        var b = new Company { Name = prefix + " B", BaseCurrency = "USD" };
        var c = new Company { Name = prefix + " C", BaseCurrency = "USD" };
        db.Companies.AddRange(a, b, c);
        db.SaveChanges();

        db.Accounts.AddRange(
            new Account { CompanyId = a.Id, Code = "100", Name = "A cash", Type = "Asset" },
            new Account { CompanyId = b.Id, Code = "200", Name = "B only", Type = "Asset" },
            new Account { CompanyId = c.Id, Code = "300", Name = "C only", Type = "Asset" });
        db.SaveChanges();

        Add(db, hasher, prefix + "-admin", "Access Admin", a, CompanyRoles.Administrator);
        Add(db, hasher, prefix + "-chief", "Access Chief", a, CompanyRoles.ChiefAccountant);
        Add(db, hasher, prefix + "-accountant", "Access Accountant", a, CompanyRoles.Accountant);
        Add(db, hasher, prefix + "-viewer", "Access Viewer", a, CompanyRoles.Viewer);
        var multi = Add(db, hasher, prefix + "-multi", "Access Multi", a, CompanyRoles.Accountant);
        db.Assignments.Add(new Assignment
        {
            UserId = multi.Id, CompanyId = b.Id, RoleId = RoleId(db, CompanyRoles.Viewer)
        });
        db.SaveChanges();
    }

    private static AppUser Add(StockiaDbContext db, IPasswordHasher hasher,
        string username, string displayName, Company company, string roleName)
    {
        var user = new AppUser
        {
            CompanyId = company.Id,
            Username = username,
            DisplayName = displayName,
            PasswordHash = hasher.Hash(Password)
        };
        db.AppUsers.Add(user);
        db.Assignments.Add(new Assignment
        {
            User = user, CompanyId = company.Id, RoleId = RoleId(db, roleName)
        });
        db.SaveChanges();
        return user;
    }

    private static int RoleId(StockiaDbContext db, string name) => db.Roles.Single(r => r.Name == name).Id;

    private static Data Read(StockiaDbContext db, string prefix)
    {
        var a = db.Companies.Single(c => c.Name == prefix + " A");
        var b = db.Companies.Single(c => c.Name == prefix + " B");
        var c = db.Companies.Single(c => c.Name == prefix + " C");
        int UserId(string role) => db.AppUsers.Single(u => u.Username == prefix + "-" + role).Id;

        return new Data(
            a.Id, b.Id, c.Id,
            UserId("admin"), UserId("chief"), UserId("accountant"), UserId("viewer"), UserId("multi"),
            db.Accounts.Single(x => x.CompanyId == a.Id && x.Code == "100").Id,
            db.Accounts.Single(x => x.CompanyId == b.Id && x.Code == "200").Id,
            RoleId(db, CompanyRoles.Viewer));
    }
}
