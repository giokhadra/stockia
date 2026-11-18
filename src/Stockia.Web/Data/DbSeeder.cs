using Stockia.Web.Domain;
using Stockia.Web.Security;
using Stockia.Web.Services;

namespace Stockia.Web.Data;

public static class DbSeeder
{
    // Development seed users. Change the passwords outside of development.
    public const string SeedUsername = "admin";
    public const string SeedPassword = "Admin#2026";
    public const string SeedCompanyName = "Demo Company";
    public const string ViewerUsername = "viewer";
    public const string ViewerPassword = "Viewer#2026";
    public const string AccountantUsername = "accountant";
    public const string AccountantPassword = "Accountant#2026";

    public static void Seed(StockiaDbContext db, IPasswordHasher hasher)
    {
        EnsureRoles(db);

        if (!db.AppUsers.Any())
        {
            var company = db.Companies.FirstOrDefault();
            if (company is null)
            {
                company = new Company { Name = SeedCompanyName };
                db.Companies.Add(company);
                db.FiscalPeriods.AddRange(FiscalPeriod.MonthsOf(company, DateTime.UtcNow.Year));   // SR-18
                db.SaveChanges();
            }

            db.AppUsers.Add(new AppUser
            {
                CompanyId = company.Id,
                Username = SeedUsername,
                PasswordHash = hasher.Hash(SeedPassword),
                DisplayName = "Administrator"
            });
            db.SaveChanges();
        }

        var admin = db.AppUsers.FirstOrDefault(u => u.Username == SeedUsername);
        if (admin is not null)
        {
            EnsureAssignment(db, admin.Id, admin.CompanyId, CompanyRoles.Administrator);
            SeedDemoUser(db, hasher, ViewerUsername, ViewerPassword, "Viewer demo", CompanyRoles.Viewer, admin.CompanyId);
            SeedDemoUser(db, hasher, AccountantUsername, AccountantPassword, "Accountant demo", CompanyRoles.Accountant, admin.CompanyId);
        }

        db.SaveChanges();
    }

    private static void EnsureRoles(StockiaDbContext db)
    {
        foreach (var name in CompanyRoles.Readers)
            if (!db.Roles.Any(r => r.Name == name))
                db.Roles.Add(new Role { Name = name });
        db.SaveChanges();
    }

    private static void EnsureAssignment(StockiaDbContext db, int userId, int companyId, string roleName)
    {
        if (db.Assignments.Any(a => a.UserId == userId && a.CompanyId == companyId)) return;
        var roleId = db.Roles.Single(r => r.Name == roleName).Id;
        db.Assignments.Add(new Assignment { UserId = userId, CompanyId = companyId, RoleId = roleId });
    }

    private static void SeedDemoUser(StockiaDbContext db, IPasswordHasher hasher,
        string username, string password, string displayName, string roleName, int companyId)
    {
        if (db.AppUsers.Any(u => u.Username == username)) return;

        var user = new AppUser
        {
            CompanyId = companyId,
            Username = username,
            PasswordHash = hasher.Hash(password),
            DisplayName = displayName
        };
        var roleId = db.Roles.Single(r => r.Name == roleName).Id;
        db.Assignments.Add(new Assignment { User = user, CompanyId = companyId, RoleId = roleId });
    }
}
