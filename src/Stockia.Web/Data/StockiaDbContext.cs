using Microsoft.EntityFrameworkCore;
using Stockia.Web.Domain;

namespace Stockia.Web.Data;

public class StockiaDbContext : DbContext
{
    public StockiaDbContext(DbContextOptions<StockiaDbContext> options) : base(options) { }

    public DbSet<Company> Companies => Set<Company>();
    public DbSet<AppUser> AppUsers => Set<AppUser>();

    // SR-39: every decimal in the model is DECIMAL(18,2) unless a property says otherwise.
    protected override void ConfigureConventions(ModelConfigurationBuilder cb)
    {
        cb.Properties<decimal>().HavePrecision(18, 2);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Company>(e =>
        {
            e.ToTable("Company");
            e.Property(c => c.Name).HasMaxLength(200).IsRequired();
        });

        b.Entity<AppUser>(e =>
        {
            e.ToTable("AppUser");
            e.HasIndex(u => u.Username).IsUnique();
            e.Property(u => u.Username).HasMaxLength(100).IsRequired();
            e.Property(u => u.PasswordHash).HasMaxLength(400).IsRequired();
            e.Property(u => u.DisplayName).HasMaxLength(150).IsRequired();
            e.HasOne(u => u.Company).WithMany().HasForeignKey(u => u.CompanyId).IsRequired();
        });
    }
}
