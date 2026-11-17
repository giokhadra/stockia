using Microsoft.EntityFrameworkCore;
using Stockia.Web.Domain;

namespace Stockia.Web.Data;

public class StockiaDbContext : DbContext
{
    public StockiaDbContext(DbContextOptions<StockiaDbContext> options) : base(options) { }

    public DbSet<Company> Companies => Set<Company>();
    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Assignment> Assignments => Set<Assignment>();

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
            e.Property(c => c.TaxNumber).HasMaxLength(30);
            e.Property(c => c.BaseCurrency).HasMaxLength(3).IsFixedLength().IsRequired();
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
                b.Entity<Role>(e =>
        {
            e.ToTable("Role");
            e.HasIndex(r => r.Name).IsUnique();
            e.Property(r => r.Name).HasMaxLength(30).IsRequired();
        });

        b.Entity<Assignment>(e =>
        {
            e.ToTable("Assignment");
            e.HasKey(a => new { a.UserId, a.CompanyId });
            e.HasOne(a => a.User).WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(a => a.Company).WithMany().HasForeignKey(a => a.CompanyId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(a => a.Role).WithMany().HasForeignKey(a => a.RoleId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
