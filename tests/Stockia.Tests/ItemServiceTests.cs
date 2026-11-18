using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Domain;
using Stockia.Web.Services;
using Xunit;

namespace Stockia.Tests;

/// <summary>SR-13 (d2): unit tests of the item rules. The in-memory database does not enforce unique indexes,
/// so the service itself checks duplicates; ItemSqlServerTests proves the real table agrees.</summary>
public class ItemServiceTests
{
    private static async Task<(ItemService Service, StockiaDbContext Db, int A, int B)> Build()
    {
        var options = new DbContextOptionsBuilder<StockiaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var db = new StockiaDbContext(options);
        var a = new Company { Name = "Company A" };
        var b = new Company { Name = "Company B" };
        db.Companies.AddRange(a, b);
        await db.SaveChangesAsync();
        return (new ItemService(db), db, a.Id, b.Id);
    }

    private static ItemForm Flour(string code = "FL-50") =>
        new(code, "Flour, 50 kg bag", "bag", "Raw materials", 38.50m, 50m, 11m, true);

    [Fact]
    public async Task Creates_an_item_in_the_company_with_all_its_fields()
    {
        var (service, db, a, _) = await Build();
        var (item, error) = await service.CreateAsync(a, Flour());

        Assert.Null(error);
        Assert.NotNull(item);
        var saved = await db.Items.SingleAsync();
        Assert.Equal(a, saved.CompanyId);
        Assert.Equal("FL-50", saved.Code);
        Assert.Equal("Flour, 50 kg bag", saved.Name);
        Assert.Equal("bag", saved.Unit);
        Assert.Equal("Raw materials", saved.Category);
        Assert.Equal(38.50m, saved.Cost);
        Assert.Equal(50m, saved.SalePrice);
        Assert.Equal(11m, saved.VatRate);
        Assert.True(saved.IsStock);
    }

    // BRD SR-13: a duplicate item code is rejected
    [Fact]
    public async Task Rejects_a_duplicate_code_in_the_same_company_whatever_the_letter_case()
    {
        var (service, db, a, _) = await Build();
        await service.CreateAsync(a, Flour("FL-50"));
        var (item, error) = await service.CreateAsync(a, Flour("fl-50"));

        Assert.Null(item);
        Assert.NotNull(error);
        Assert.Equal(1, await db.Items.CountAsync());
    }

    [Fact]
    public async Task Allows_the_same_code_in_another_company()
    {
        var (service, db, a, b) = await Build();
        await service.CreateAsync(a, Flour());
        var (item, error) = await service.CreateAsync(b, Flour());

        Assert.Null(error);
        Assert.NotNull(item);
        Assert.Equal(2, await db.Items.CountAsync());
    }

    [Fact]
    public async Task Lists_only_the_items_of_the_company()
    {
        var (service, _, a, b) = await Build();
        await service.CreateAsync(a, Flour("FL-50"));
        await service.CreateAsync(b, Flour("SG-25"));

        var list = await service.ListAsync(a);
        Assert.Equal(new[] { "FL-50" }, list.Select(x => x.Code));
    }

    // BRD SR-13: each item carries its VAT rate: 11%, 0% (exports) or another rate
    [Theory]
    [InlineData(11)]
    [InlineData(0)]
    [InlineData(4.5)]
    public async Task Keeps_the_vat_rate_given_to_the_item(double rate)
    {
        var (service, db, a, _) = await Build();
        var form = Flour() with { VatRate = (decimal)rate };
        var (item, error) = await service.CreateAsync(a, form);

        Assert.Null(error);
        Assert.Equal((decimal)rate, (await db.Items.SingleAsync()).VatRate);
    }

    [Theory]
    [InlineData(null, "Flour", "bag")]     // no code
    [InlineData(" ", "Flour", "bag")]
    [InlineData("FL-50", " ", "bag")]      // no name
    [InlineData("FL-50", "Flour", " ")]    // no unit
    public async Task Refuses_an_empty_code_name_or_unit(string? code, string name, string unit)
    {
        var (service, db, a, _) = await Build();
        var (item, error) = await service.CreateAsync(a, new ItemForm(code, name, unit, null, 1m, 1m, 11m, true));

        Assert.Null(item);
        Assert.NotNull(error);
        Assert.Empty(db.Items);
    }

    [Fact]
    public async Task Refuses_missing_negative_or_impossible_amounts()
    {
        var (service, db, a, _) = await Build();
        Assert.NotNull((await service.CreateAsync(a, Flour() with { SalePrice = null })).Error);
        Assert.NotNull((await service.CreateAsync(a, Flour() with { SalePrice = -1m })).Error);
        Assert.NotNull((await service.CreateAsync(a, Flour() with { Cost = -1m })).Error);
        Assert.NotNull((await service.CreateAsync(a, Flour() with { VatRate = null })).Error);
        Assert.NotNull((await service.CreateAsync(a, Flour() with { VatRate = -1m })).Error);
        Assert.NotNull((await service.CreateAsync(a, Flour() with { VatRate = 101m })).Error);
        Assert.Empty(db.Items);
    }

    // SR-39: amounts are kept with 2 places, rounded half away from zero like every other amount
    [Fact]
    public async Task Rounds_cost_and_prices_to_two_places()
    {
        var (service, db, a, _) = await Build();
        await service.CreateAsync(a, Flour() with { Cost = 10.005m, SalePrice = 20.004m });

        var saved = await db.Items.SingleAsync();
        Assert.Equal(10.01m, saved.Cost);
        Assert.Equal(20.00m, saved.SalePrice);
    }

    [Fact]
    public async Task A_blank_cost_is_zero_and_a_blank_category_is_empty()
    {
        var (service, db, a, _) = await Build();
        await service.CreateAsync(a, Flour() with { Cost = null, Category = "  " });

        var saved = await db.Items.SingleAsync();
        Assert.Equal(0m, saved.Cost);
        Assert.Null(saved.Category);
    }

    [Fact]
    public async Task A_service_is_not_a_stock_item()
    {
        var (service, db, a, _) = await Build();
        await service.CreateAsync(a, new ItemForm("DLV", "Delivery", "trip", "Services", 0m, 15m, 11m, false));

        Assert.False((await db.Items.SingleAsync()).IsStock);
    }

    [Fact]
    public async Task Edits_everything_but_the_code_including_the_vat_rate()
    {
        var (service, db, a, _) = await Build();
        var (item, _) = await service.CreateAsync(a, Flour());

        var (updated, error) = await service.UpdateAsync(a, item!.Id,
            new ItemForm("IGNORED", "Flour, 25 kg bag", "bag25", "Food", 20m, 28m, 0m, true));

        Assert.Null(error);
        Assert.Equal("FL-50", updated!.Code);
        Assert.Equal("Flour, 25 kg bag", updated.Name);
        Assert.Equal("bag25", updated.Unit);
        Assert.Equal(28m, updated.SalePrice);
        Assert.Equal(0m, updated.VatRate);
    }

    [Fact]
    public async Task Cannot_edit_or_find_an_item_of_another_company()
    {
        var (service, _, a, b) = await Build();
        var (itemOfB, _) = await service.CreateAsync(b, Flour());

        Assert.Null(await service.FindAsync(a, itemOfB!.Id));
        Assert.NotNull((await service.UpdateAsync(a, itemOfB.Id, Flour())).Error);
    }
}