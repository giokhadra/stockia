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
        public DbSet<Account> Accounts => Set<Account>();
    public DbSet<FiscalPeriod> FiscalPeriods => Set<FiscalPeriod>();
    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
    public DbSet<JournalLine> JournalLines => Set<JournalLine>();

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
                b.Entity<Account>(e =>
        {
            e.ToTable("Account");
            e.HasIndex(a => new { a.CompanyId, a.Code }).IsUnique();
            e.Property(a => a.Code).HasMaxLength(20).IsRequired();
            e.Property(a => a.Name).HasMaxLength(100).IsRequired();
            e.Property(a => a.Type).HasMaxLength(20).IsRequired();
            e.HasOne<Company>().WithMany().HasForeignKey(a => a.CompanyId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Account>().WithMany().HasForeignKey(a => a.ParentId).OnDelete(DeleteBehavior.Restrict);
        });

        // SR-18: twelve monthly periods per company; one period per company and start date.
        b.Entity<FiscalPeriod>(e =>
        {
            e.ToTable("FiscalPeriod");
            e.HasIndex(p => new { p.CompanyId, p.StartDate }).IsUnique();
            e.Property(p => p.Status).HasMaxLength(10).IsRequired();
            e.HasOne(p => p.Company).WithMany().HasForeignKey(p => p.CompanyId).OnDelete(DeleteBehavior.Restrict);
        });

        // SR-2: manual journal entries. Every table has CompanyId (SR-33); amounts use DECIMAL(18,2) (SR-39).
        b.Entity<JournalEntry>(e =>
        {
            e.ToTable("JournalEntry");
            e.Property(j => j.Date).HasColumnType("date");
            e.Property(j => j.Reference).HasMaxLength(50).IsRequired();
            e.Property(j => j.Description).HasMaxLength(500).IsRequired();
            e.HasOne<Company>().WithMany().HasForeignKey(j => j.CompanyId).IsRequired().OnDelete(DeleteBehavior.Restrict);
            e.HasMany(j => j.Lines).WithOne().HasForeignKey(l => l.JournalEntryId).IsRequired().OnDelete(DeleteBehavior.Cascade);
            e.Navigation(j => j.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        b.Entity<JournalLine>(e =>
        {
            e.ToTable("JournalLine");
            e.HasIndex(l => l.AccountId);
            e.HasOne<Company>().WithMany().HasForeignKey(l => l.CompanyId).IsRequired().OnDelete(DeleteBehavior.Restrict);
            e.HasOne(l => l.Account).WithMany().HasForeignKey(l => l.AccountId).IsRequired().OnDelete(DeleteBehavior.Restrict);
        });
    }
}
