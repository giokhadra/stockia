using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Domain;
using Stockia.Web.Services;
using Xunit;

namespace Stockia.Tests;

/// <summary>Runs only when STOCKIA_TEST_CONNECTION is set (GitHub Actions, after db/007_item_warehouse_v1.sql).
/// Proves the EF model and the real Item and Warehouse tables agree, and that the table itself refuses a
/// duplicate code even when the service is bypassed.</summary>
[Collection("SqlServer")]
public class ItemSqlServerTests
{
    [SqlServerFact]
    public async Task Items_and_warehouses_are_saved_and_the_table_refuses_a_duplicate_code()
    {
        var options = new DbContextOptionsBuilder<StockiaDbContext>()
            .UseSqlServer(Environment.GetEnvironmentVariable("STOCKIA_TEST_CONNECTION")).Options;
        using var db = new StockiaDbContext(options);

        var (company, error) = await new CompanyService(db)
            .CreateAsync("SQL items " + Guid.NewGuid().ToString("N")[..8], null, "USD");
        Assert.Null(error);

        var (item, itemError) = await new ItemService(db).CreateAsync(company!.Id,
            new ItemForm("FL-50", "Flour, 50 kg bag", "bag", "Raw materials", 38.50m, 50m, 11m, true));
        Assert.Null(itemError);

        var (warehouse, warehouseError) = await new WarehouseService(db).CreateAsync(company.Id, "Main warehouse");
        Assert.Null(warehouseError);

        var items = await new ItemService(db).ListAsync(company.Id);
        Assert.Equal(50m, Assert.Single(items).SalePrice);
        Assert.Equal("Main warehouse", Assert.Single(await new WarehouseService(db).ListAsync(company.Id)).Name);

        // UNIQUE (CompanyId, Code) in the table, not only in the service
        db.Items.Add(new Item
        {
            CompanyId = company.Id, Code = "FL-50", Name = "Duplicate", Unit = "bag", SalePrice = 1m, VatRate = 11m
        });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}