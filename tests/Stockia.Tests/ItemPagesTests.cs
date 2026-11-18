using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Stockia.Web.Data;
using Stockia.Web.Domain;
using Xunit;

namespace Stockia.Tests;

/// <summary>System test of SR-13 (d3) with the roles and companies of SR-33: the pages for items and
/// warehouses, as each role sees them. Writers add and edit, a viewer only looks, nobody reaches another company's rows.</summary>
public class ItemPagesTests : IClassFixture<StockiaFactory>
{
    private readonly StockiaFactory _factory;
    private readonly AccessSeeding.Data _data;

    public ItemPagesTests(StockiaFactory factory)
    {
        _factory = factory;
        _data = AccessSeeding.Ensure(factory, "items");
    }

    private async Task<HttpClient> LoginAsync(string role)
    {
        var client = TestClient.Https(_factory);
        var response = await TestClient.LoginAsync(client, "items-" + role, AccessSeeding.Password);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }

    private static Dictionary<string, string> ItemForm(string code, string name = "Flour") => new()
    {
        ["Code"] = code, ["Name"] = name, ["Unit"] = "bag", ["Category"] = "Raw materials",
        ["Cost"] = "38.50", ["SalePrice"] = "50.00", ["VatRate"] = "11", ["IsStock"] = "true"
    };

    private int SeedItem(int companyId, string code, string name)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockiaDbContext>();
        var item = new Item
        {
            CompanyId = companyId, Code = code, Name = name, Unit = "bag", SalePrice = 1m, VatRate = 11m, IsStock = true
        };
        db.Items.Add(item);
        db.SaveChanges();
        return item.Id;
    }

    [Fact]
    public async Task Every_role_may_open_the_items_and_warehouses_lists()
    {
        foreach (var role in new[] { "admin", "chief", "accountant", "viewer" })
        {
            var client = await LoginAsync(role);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Items")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Warehouses")).StatusCode);
        }
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("chief")]
    [InlineData("accountant")]
    public async Task Writers_can_add_an_item_in_their_company(string role)
    {
        var client = await LoginAsync(role);
        var code = "I" + Guid.NewGuid().ToString("N")[..8];
        var response = await TestClient.PostFormAsync(client, "/Items", "/Items/Create", ItemForm(code));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockiaDbContext>();
        var saved = db.Items.Single(i => i.CompanyId == _data.CompanyAId && i.Code == code);
        Assert.Equal(50m, saved.SalePrice);
        Assert.Equal(11m, saved.VatRate);
    }

    // BRD SR-13: a duplicate item code is rejected
    [Fact]
    public async Task A_duplicate_item_code_is_refused_with_a_message_and_no_second_row()
    {
        var client = await LoginAsync("accountant");
        var code = "D" + Guid.NewGuid().ToString("N")[..8];
        await TestClient.PostFormAsync(client, "/Items", "/Items/Create", ItemForm(code));
        var second = await TestClient.PostFormAsync(client, "/Items", "/Items/Create", ItemForm(code, "Other"));

        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Contains("already exists", await second.Content.ReadAsStringAsync());

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockiaDbContext>();
        Assert.Equal(1, db.Items.Count(i => i.CompanyId == _data.CompanyAId && i.Code == code));
    }

    [Fact]
    public async Task Viewer_cannot_add_an_item_or_a_warehouse_and_no_row_is_written()
    {
        var client = await LoginAsync("viewer");
        var code = "V" + Guid.NewGuid().ToString("N")[..8];
        var name = "Viewer depot " + Guid.NewGuid().ToString("N")[..6];

        var item = await TestClient.PostFormAsync(client, "/Items", "/Items/Create", ItemForm(code));
        var warehouse = await TestClient.PostFormAsync(client, "/Warehouses", "/Warehouses/Create",
            new Dictionary<string, string> { ["Name"] = name });

        Assert.Equal(HttpStatusCode.Forbidden, item.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, warehouse.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockiaDbContext>();
        Assert.False(db.Items.Any(i => i.Code == code));
        Assert.False(db.Warehouses.Any(w => w.Name == name));
    }

    [Fact]
    public async Task Viewer_cannot_open_an_edit_form()
    {
        var itemId = SeedItem(_data.CompanyAId, "E" + Guid.NewGuid().ToString("N")[..8], "Editable");
        var client = await LoginAsync("viewer");

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/Items/Edit/{itemId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/Warehouses/Edit/1")).StatusCode);
    }

    [Fact]
    public async Task Accountant_edits_an_item_name_and_vat_rate_but_the_code_stays()
    {
        var code = "U" + Guid.NewGuid().ToString("N")[..8];
        var itemId = SeedItem(_data.CompanyAId, code, "Before");
        var client = await LoginAsync("accountant");

        var response = await TestClient.PostFormAsync(client, $"/Items/Edit/{itemId}", "/Items/Edit",
            new Dictionary<string, string>
            {
                ["Id"] = itemId.ToString(), ["Name"] = "After", ["Unit"] = "kg", ["Category"] = "",
                ["Cost"] = "1.00", ["SalePrice"] = "2.00", ["VatRate"] = "0", ["IsStock"] = "false"
            });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockiaDbContext>();
        var saved = db.Items.Single(i => i.Id == itemId);
        Assert.Equal(code, saved.Code);
        Assert.Equal("After", saved.Name);
        Assert.Equal(0m, saved.VatRate);
        Assert.False(saved.IsStock);
    }

    // BRD SR-18 / SR-33: an entry in Company A never appears in Company B
    [Fact]
    public async Task Accountant_sees_only_own_company_items_and_cannot_open_another_company_item()
    {
        var own = SeedItem(_data.CompanyAId, "OA" + Guid.NewGuid().ToString("N")[..6], "Alpha-only item");
        var other = SeedItem(_data.CompanyBId, "OB" + Guid.NewGuid().ToString("N")[..6], "Beta-only item");
        var client = await LoginAsync("accountant");

        var page = await client.GetStringAsync("/Items");
        Assert.Contains("Alpha-only item", page);
        Assert.DoesNotContain("Beta-only item", page);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/Items/Edit/{own}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/Items/Edit/{other}")).StatusCode);
    }

    [Fact]
    public async Task Writers_add_and_rename_warehouses_and_a_duplicate_name_is_refused()
    {
        var client = await LoginAsync("chief");
        var name = "Depot " + Guid.NewGuid().ToString("N")[..8];

        var created = await TestClient.PostFormAsync(client, "/Warehouses", "/Warehouses/Create",
            new Dictionary<string, string> { ["Name"] = name });
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);

        var duplicate = await TestClient.PostFormAsync(client, "/Warehouses", "/Warehouses/Create",
            new Dictionary<string, string> { ["Name"] = name });
        Assert.Equal(HttpStatusCode.OK, duplicate.StatusCode);
        Assert.Contains("already exists", await duplicate.Content.ReadAsStringAsync());

        int id;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StockiaDbContext>();
            Assert.Equal(1, db.Warehouses.Count(w => w.CompanyId == _data.CompanyAId && w.Name == name));
            id = db.Warehouses.Single(w => w.CompanyId == _data.CompanyAId && w.Name == name).Id;
        }

        var renamed = await TestClient.PostFormAsync(client, $"/Warehouses/Edit/{id}", "/Warehouses/Edit",
            new Dictionary<string, string> { ["Id"] = id.ToString(), ["Name"] = name + " 2" });
        Assert.Equal(HttpStatusCode.Redirect, renamed.StatusCode);

        using var scope2 = _factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<StockiaDbContext>();
        Assert.Equal(name + " 2", db2.Warehouses.Single(w => w.Id == id).Name);
    }
}