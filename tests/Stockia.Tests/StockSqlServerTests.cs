using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Domain;
using Stockia.Web.Services;
using Xunit;

namespace Stockia.Tests;

/// <summary>Runs only when STOCKIA_TEST_CONNECTION is set (GitHub Actions, after db/012_stock_movement_v1.sql).
/// Proves the EF mapping and the real StockMovement table and Invoice.WarehouseId agree, and that the table itself
/// receives an invoice line only once.</summary>
[Collection("SqlServer")]
public class StockSqlServerTests
{
    [SqlServerFact]
    public async Task A_movement_round_trips_and_the_table_receives_an_invoice_line_only_once()
    {
        var options = new DbContextOptionsBuilder<StockiaDbContext>()
            .UseSqlServer(Environment.GetEnvironmentVariable("STOCKIA_TEST_CONNECTION")).Options;
        using var db = new StockiaDbContext(options);
        DbSeeder.Seed(db, new BcryptPasswordHasher());   // the seeder skips a database that already has users, so run it first

        var company = new Company { Name = "SQL stock " + Guid.NewGuid().ToString("N")[..8], BaseCurrency = "USD" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var item = new Item { CompanyId = company.Id, Code = "FL", Name = "Flour", Unit = "bag", VatRate = 11m, IsStock = true };
        var warehouse = new Warehouse { CompanyId = company.Id, Name = "Main" };
        var supplier = new Supplier { CompanyId = company.Id, Name = "Beirut Mills" };
        db.AddRange(item, warehouse, supplier);
        await db.SaveChangesAsync();

        var invoice = new PurchaseInvoice
        {
            CompanyId = company.Id, PartnerId = supplier.Id, Number = "PI-1", Currency = "USD", WarehouseId = warehouse.Id,
            Date = new DateTime(2026, 11, 24), DueDate = new DateTime(2026, 12, 24)
        };
        invoice.Lines.Add(new InvoiceLine { CompanyId = company.Id, ItemId = item.Id, Description = "Flour", Quantity = 10m, UnitPrice = 20.35m, VatRate = 11m, IsStock = true });
        invoice.Recalculate();
        db.PurchaseInvoices.Add(invoice);
        await db.SaveChangesAsync();

        new StockService(db).ReceivePurchase(invoice, warehouse.Id, 1m);
        await db.SaveChangesAsync();

        using (var db2 = new StockiaDbContext(options))
        {
            var stock = new StockService(db2);
            Assert.Equal(10m, await stock.QuantityAsync(company.Id, item.Id, warehouse.Id));
            Assert.Equal(203.50m, await stock.ValueAsync(company.Id, item.Id));          // DECIMAL(18,2), exact
            Assert.Equal(warehouse.Id, (await db2.PurchaseInvoices.SingleAsync(i => i.Id == invoice.Id)).WarehouseId);
        }

        // UX_StockMovement_InvoiceLineId in the table, not only in the service
        db.StockMovements.Add(new StockMovement
        {
            CompanyId = company.Id, ItemId = item.Id, WarehouseId = warehouse.Id, InvoiceLineId = invoice.Lines[0].Id,
            Date = invoice.Date, Type = StockMovementTypes.In, Quantity = 10m, Value = 203.50m
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}