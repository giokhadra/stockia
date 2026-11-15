using Stockia.Web.Domain;
using Stockia.Web.Services;

namespace Stockia.Web.Data;

public static class DbSeeder
{
    // Development seed user. Change the password outside of development.
    public const string SeedUsername = "admin";
    public const string SeedPassword = "Admin#2026";
    public const string SeedCompanyName = "Demo Company";

    public static void Seed(StockiaDbContext db, IPasswordHasher hasher)
    {
        if (db.AppUsers.Any()) return;

        var company = db.Companies.FirstOrDefault();
        if (company is null)
        {
            company = new Company { Name = SeedCompanyName };
            db.Companies.Add(company);
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
}
