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
    public DbSet<ExchangeRate> ExchangeRates => Set<ExchangeRate>();
     public DbSet<Item> Items => Set<Item>();
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    public DbSet<Partner> Partners => Set<Partner>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<PurchaseInvoice> PurchaseInvoices => Set<PurchaseInvoice>();
    public DbSet<InvoiceLine> InvoiceLines => Set<InvoiceLine>();
        public DbSet<StockMovement> StockMovements => Set<StockMovement>();
        public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    // SR-39: every decimal in the model is DECIMAL(18,2) unless a property says otherwise (only exchange rates: DECIMAL(20,10)).
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
            // SR-19: a foreign line keeps its currency, foreign amount (DECIMAL(18,2) by convention) and rate.
            e.Property(l => l.Currency).HasMaxLength(3).IsFixedLength().IsUnicode(false);
            e.Property(l => l.Rate).HasPrecision(20, ExchangeRate.RateDecimals);
        });

        // SR-19: one rate per company, currency and date (SR-33: CompanyId).
        b.Entity<ExchangeRate>(e =>
        {
            e.ToTable("ExchangeRate");
            e.HasIndex(r => new { r.CompanyId, r.Currency, r.Date }).IsUnique();
            e.Property(r => r.Currency).HasMaxLength(3).IsFixedLength().IsUnicode(false).IsRequired();
            e.Property(r => r.Date).HasColumnType("date");
            e.Property(r => r.Rate).HasPrecision(20, ExchangeRate.RateDecimals);
            e.HasOne<Company>().WithMany().HasForeignKey(r => r.CompanyId).IsRequired().OnDelete(DeleteBehavior.Restrict);
        });
         // SR-13: items and warehouses of a company. CompanyId on both (SR-33); decimals are DECIMAL(18,2) by convention (SR-39).
        b.Entity<Item>(e =>
        {
            e.ToTable("Item");
            e.HasIndex(i => new { i.CompanyId, i.Code }).IsUnique();
            e.Property(i => i.Code).HasMaxLength(20).IsRequired();
            e.Property(i => i.Name).HasMaxLength(100).IsRequired();
            e.Property(i => i.Unit).HasMaxLength(10).IsRequired();
            e.Property(i => i.Category).HasMaxLength(50);
            e.HasOne<Company>().WithMany().HasForeignKey(i => i.CompanyId).IsRequired().OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Warehouse>(e =>
        {
            e.ToTable("Warehouse");
            e.HasIndex(w => new { w.CompanyId, w.Name }).IsUnique();
            e.Property(w => w.Name).HasMaxLength(100).IsRequired();
            e.HasOne<Company>().WithMany().HasForeignKey(w => w.CompanyId).IsRequired().OnDelete(DeleteBehavior.Restrict);
        });

        // SR-7: partners (one table, Kind tells a supplier from a future customer) and invoices (same idea).
        // CompanyId on every table (SR-33); amounts DECIMAL(18,2) by convention (SR-39).
        b.Entity<Partner>(e =>
        {
            e.ToTable("Partner");
            e.HasDiscriminator<string>("Kind").HasValue<Supplier>("Supplier");
            e.Property("Kind").HasMaxLength(20);
            e.HasIndex(p => new { p.CompanyId, p.Name }).IsUnique();
            e.Property(p => p.Name).HasMaxLength(150).IsRequired();
            e.Property(p => p.TaxNumber).HasMaxLength(30);
            e.Property(p => p.Phone).HasMaxLength(30);
            e.HasOne<Company>().WithMany().HasForeignKey(p => p.CompanyId).IsRequired().OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Invoice>(e =>
        {
            e.ToTable("Invoice");
            e.HasDiscriminator<string>("Kind").HasValue<PurchaseInvoice>("Purchase");
            e.Property("Kind").HasMaxLength(20);
            e.HasIndex("CompanyId", "Kind", nameof(Invoice.PartnerId), nameof(Invoice.Number)).IsUnique();
            e.Property(i => i.Number).HasMaxLength(50).IsRequired();
            e.Property(i => i.Date).HasColumnType("date");
            e.Property(i => i.DueDate).HasColumnType("date");
            e.Property(i => i.Currency).HasMaxLength(3).IsFixedLength().IsUnicode(false).IsRequired();
            e.Property(i => i.Description).HasMaxLength(500);
            e.HasOne<Company>().WithMany().HasForeignKey(i => i.CompanyId).IsRequired().OnDelete(DeleteBehavior.Restrict);
            e.HasOne(i => i.Partner).WithMany().HasForeignKey(i => i.PartnerId).IsRequired().OnDelete(DeleteBehavior.Restrict);
            e.HasMany(i => i.Lines).WithOne().HasForeignKey(l => l.InvoiceId).IsRequired().OnDelete(DeleteBehavior.Cascade);
                        e.HasOne(i => i.Warehouse).WithMany().HasForeignKey(i => i.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<InvoiceLine>(e =>
        {
            e.ToTable("InvoiceLine");
            e.Property(l => l.Description).HasMaxLength(100).IsRequired();
            e.HasOne<Company>().WithMany().HasForeignKey(l => l.CompanyId).IsRequired().OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Item>().WithMany().HasForeignKey(l => l.ItemId).IsRequired().OnDelete(DeleteBehavior.Restrict);
        });
        
        // SR-14: stock movements. CompanyId on the table (SR-33); Quantity and Value are DECIMAL(18,2) by convention (SR-39).
        b.Entity<StockMovement>(e =>
        {
            e.ToTable("StockMovement");
            e.HasIndex(m => new { m.CompanyId, m.ItemId, m.WarehouseId });
            e.HasIndex(m => m.InvoiceLineId).IsUnique().HasFilter("[InvoiceLineId] IS NOT NULL");
            e.Property(m => m.Type).HasMaxLength(10).IsRequired();
            e.Property(m => m.Date).HasColumnType("date");
            e.HasOne<Company>().WithMany().HasForeignKey(m => m.CompanyId).IsRequired().OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Item>().WithMany().HasForeignKey(m => m.ItemId).IsRequired().OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Warehouse>().WithMany().HasForeignKey(m => m.WarehouseId).IsRequired().OnDelete(DeleteBehavior.Restrict);
            e.HasOne<InvoiceLine>().WithMany().HasForeignKey(m => m.InvoiceLineId).OnDelete(DeleteBehavior.Restrict);
        });
        
        // SR-6: the audit trail (CompanyId on it, SR-33) and the link from a reversal to the entry it reverses.
        b.Entity<AuditLog>(e =>
        {
            e.ToTable("AuditLog");
            e.HasIndex(a => new { a.CompanyId, a.LogTime });
            e.Property(a => a.Action).HasMaxLength(50).IsRequired();
            e.Property(a => a.Details).HasMaxLength(400);
            e.HasOne<Company>().WithMany().HasForeignKey(a => a.CompanyId).IsRequired().OnDelete(DeleteBehavior.Restrict);
            e.HasOne(a => a.User).WithMany().HasForeignKey(a => a.UserId).IsRequired().OnDelete(DeleteBehavior.Restrict);
            e.HasOne(a => a.Entry).WithMany().HasForeignKey(a => a.EntryId).OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<JournalEntry>(e =>
        {
            e.HasOne<JournalEntry>().WithMany().HasForeignKey(j => j.ReversesEntryId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(j => j.ReversesEntryId).IsUnique().HasFilter("[ReversesEntryId] IS NOT NULL");

            // SR-4: the document an automatic entry comes from; a document is posted only once per company.
            e.Property(j => j.SourceType).HasMaxLength(30);
            e.HasIndex(j => new { j.CompanyId, j.SourceType, j.SourceId }).IsUnique().HasFilter("[SourceType] IS NOT NULL");
        });
    }
}
    

