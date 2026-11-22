using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Domain;
using Xunit;

namespace Stockia.Tests;

/// <summary>Runs only when STOCKIA_TEST_CONNECTION is set (GitHub Actions, after db/011_supplier_purchase_invoice_v1.sql).
/// Proves the EF mapping and the real Partner, Invoice and InvoiceLine tables agree, and that the amounts are DECIMAL.</summary>
[Collection("SqlServer")]
public class PurchaseInvoiceSqlServerTests
{
    [SqlServerFact]
    public async Task A_purchase_invoice_round_trips_through_the_real_tables_and_the_table_refuses_a_duplicate_number()
    {
        var options = new DbContextOptionsBuilder<StockiaDbContext>()
            .UseSqlServer(Environment.GetEnvironmentVariable("STOCKIA_TEST_CONNECTION")).Options;
        using var db = new StockiaDbContext(options);

        var company = new Company { Name = "SQL purchases " + Guid.NewGuid().ToString("N")[..8], BaseCurrency = "USD" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var item = new Item { CompanyId = company.Id, Code = "FL", Name = "Flour", Unit = "bag", SalePrice = 1m, VatRate = 11m };
        var supplier = new Supplier { CompanyId = company.Id, Name = "Beirut Mills" };
        db.AddRange(item, supplier);
        await db.SaveChangesAsync();

        PurchaseInvoice Make() => new()
        {
            CompanyId = company.Id, PartnerId = supplier.Id, Number = "PI-1", Currency = "USD",
            Date = new DateTime(2026, 11, 24), DueDate = new DateTime(2026, 12, 24)
        };
        var invoice = Make();
        invoice.Lines.Add(new InvoiceLine { CompanyId = company.Id, ItemId = item.Id, Description = "Flour", Quantity = 3m, UnitPrice = 0.35m, VatRate = 11m });
        invoice.Recalculate();
        db.PurchaseInvoices.Add(invoice);
        await db.SaveChangesAsync();

        using var db2 = new StockiaDbContext(options);
        var loaded = await db2.PurchaseInvoices.Include(i => i.Lines).SingleAsync(i => i.Id == invoice.Id);
        Assert.Equal(1.05m, loaded.Subtotal);
        Assert.Equal(0.12m, loaded.VatTotal);        // 1.05 x 11% = 0.1155 -> 0.12
        Assert.Equal(1.17m, loaded.Total);
        Assert.Single(loaded.Lines);

        db2.PurchaseInvoices.Add(Make());
        await Assert.ThrowsAsync<DbUpdateException>(() => db2.SaveChangesAsync());
    }
}
