using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Domain;
using Stockia.Web.Services;
using Xunit;

namespace Stockia.Tests;

/// <summary>Runs only when STOCKIA_TEST_CONNECTION is set (GitHub Actions, after db/014_sale_approval_v1.sql).
/// Proves the EF mapping and the real Invoice.Status / DecidedByUserId / DecidedAt columns agree, that an "Out"
/// movement with a negative quantity is stored exactly, and that the table refuses an unknown status.</summary>
[Collection("SqlServer")]
public class StockSaleSqlServerTests
{
    [SqlServerFact]
    public async Task A_decided_sale_and_its_out_movement_round_trip_and_the_table_refuses_an_unknown_status()
    {
        var options = new DbContextOptionsBuilder<StockiaDbContext>()
            .UseSqlServer(Environment.GetEnvironmentVariable("STOCKIA_TEST_CONNECTION")).Options;
        using var db = new StockiaDbContext(options);
        DbSeeder.Seed(db, new BcryptPasswordHasher());   // the seeder skips a database that already has users, so run it first

        var company = new Company { Name = "SQL sale " + Guid.NewGuid().ToString("N")[..8], BaseCurrency = "USD" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        var user = new AppUser { CompanyId = company.Id, Username = "sql-chief-" + Guid.NewGuid().ToString("N")[..8], PasswordHash = "x", DisplayName = "Chief" };
        var item = new Item { CompanyId = company.Id, Code = "FL", Name = "Flour", Unit = "bag", VatRate = 11m, IsStock = true };
        var warehouse = new Warehouse { CompanyId = company.Id, Name = "Main" };
        var customer = new Customer { CompanyId = company.Id, Name = "Byblos Bakery" };
        db.AddRange(user, item, warehouse, customer);
        await db.SaveChangesAsync();

        var invoice = new SalesInvoice
        {
            CompanyId = company.Id, PartnerId = customer.Id, Number = "S-" + Guid.NewGuid().ToString("N")[..6], Currency = "USD",
            WarehouseId = warehouse.Id, Date = new DateTime(2026, 11, 26), DueDate = new DateTime(2026, 12, 26),
            Status = InvoiceStatuses.Posted, DecidedByUserId = user.Id, DecidedAt = new DateTime(2026, 11, 26, 9, 0, 0)
        };
        invoice.Lines.Add(new InvoiceLine { CompanyId = company.Id, ItemId = item.Id, Description = "Flour", Quantity = 3m, UnitPrice = 15m, VatRate = 11m, IsStock = true });
        invoice.Recalculate();
        db.SalesInvoices.Add(invoice);
        db.StockMovements.Add(new StockMovement { CompanyId = company.Id, ItemId = item.Id, WarehouseId = warehouse.Id,
            Date = new DateTime(2026, 11, 1), Type = StockMovementTypes.In, Quantity = 10m, Value = 100.01m });
        await db.SaveChangesAsync();

        await new StockService(db).IssueSaleAsync(invoice, warehouse.Id);
        await db.SaveChangesAsync();

        using (var db2 = new StockiaDbContext(options))
        {
            var saved = await db2.SalesInvoices.SingleAsync(i => i.Id == invoice.Id);
            Assert.Equal(InvoiceStatuses.Posted, saved.Status);
            Assert.Equal(user.Id, saved.DecidedByUserId);
            var stock = new StockService(db2);
            Assert.Equal(7m, await stock.QuantityAsync(company.Id, item.Id, warehouse.Id));
            Assert.Equal(70.01m, await stock.ValueAsync(company.Id, item.Id));      // 100.01 - 3 x 10.001 -> 30.00
        }

        // CK_Invoice_Status in the table, not only in the code
        await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(
            "UPDATE dbo.Invoice SET Status = 'Maybe' WHERE Id = {0}", invoice.Id));
    }
}