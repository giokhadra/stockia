using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Domain;
using Stockia.Web.Services;
using Xunit;

namespace Stockia.Tests;

/// <summary>SR-13 (d2): unit tests of the warehouse rules.</summary>
public class WarehouseServiceTests
{
    private static async Task<(WarehouseService Service, StockiaDbContext Db, int A, int B)> Build()
    {
        var options = new DbContextOptionsBuilder<StockiaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var db = new StockiaDbContext(options);
        var a = new Company { Name = "Company A" };
        var b = new Company { Name = "Company B" };
        db.Companies.AddRange(a, b);
        await db.SaveChangesAsync();
        return (new WarehouseService(db), db, a.Id, b.Id);
    }

    [Fact]
    public async Task Creates_a_warehouse_in_the_company()
    {
        var (service, db, a, _) = await Build();
        var (warehouse, error) = await service.CreateAsync(a, "  Main warehouse, Beirut ");

        Assert.Null(error);
        Assert.NotNull(warehouse);
        var saved = await db.Warehouses.SingleAsync();
        Assert.Equal(a, saved.CompanyId);
        Assert.Equal("Main warehouse, Beirut", saved.Name);
    }

    [Fact]
    public async Task Refuses_an_empty_name()
    {
        var (service, db, a, _) = await Build();
        Assert.NotNull((await service.CreateAsync(a, " ")).Error);
        Assert.NotNull((await service.CreateAsync(a, null)).Error);
        Assert.Empty(db.Warehouses);
    }

    [Fact]
    public async Task Rejects_a_second_warehouse_with_the_same_name_in_the_company()
    {
        var (service, db, a, _) = await Build();
        await service.CreateAsync(a, "Zahle depot");
        var (warehouse, error) = await service.CreateAsync(a, "zahle DEPOT");

        Assert.Null(warehouse);
        Assert.NotNull(error);
        Assert.Equal(1, await db.Warehouses.CountAsync());
    }

    [Fact]
    public async Task Allows_the_same_name_in_another_company()
    {
        var (service, db, a, b) = await Build();
        await service.CreateAsync(a, "Main warehouse");
        var (warehouse, error) = await service.CreateAsync(b, "Main warehouse");

        Assert.Null(error);
        Assert.NotNull(warehouse);
        Assert.Equal(2, await db.Warehouses.CountAsync());
    }

    [Fact]
    public async Task Lists_only_the_warehouses_of_the_company()
    {
        var (service, _, a, b) = await Build();
        await service.CreateAsync(a, "Zahle depot");
        await service.CreateAsync(a, "Main warehouse");
        await service.CreateAsync(b, "Byblos store");

        var list = await service.ListAsync(a);
        Assert.Equal(new[] { "Main warehouse", "Zahle depot" }, list.Select(x => x.Name));
    }

    [Fact]
    public async Task Renames_a_warehouse_but_not_to_a_name_already_used()
    {
        var (service, _, a, _) = await Build();
        var (first, _) = await service.CreateAsync(a, "Main warehouse");
        await service.CreateAsync(a, "Zahle depot");

        var (renamed, error) = await service.UpdateAsync(a, first!.Id, "Main warehouse, Beirut");
        Assert.Null(error);
        Assert.Equal("Main warehouse, Beirut", renamed!.Name);

        Assert.NotNull((await service.UpdateAsync(a, first.Id, "Zahle depot")).Error);
        // saving it again under its own name is not a duplicate
        Assert.Null((await service.UpdateAsync(a, first.Id, "Main warehouse, Beirut")).Error);
    }

    [Fact]
    public async Task Cannot_find_or_rename_a_warehouse_of_another_company()
    {
        var (service, _, a, b) = await Build();
        var (ofB, _) = await service.CreateAsync(b, "Byblos store");

        Assert.Null(await service.FindAsync(a, ofB!.Id));
        Assert.NotNull((await service.UpdateAsync(a, ofB.Id, "Stolen")).Error);
    }
}