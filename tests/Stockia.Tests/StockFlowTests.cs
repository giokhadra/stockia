using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Stockia.Web.Data;
using Stockia.Web.Domain;
using Stockia.Web.Services;
using Xunit;

namespace Stockia.Tests;

/// <summary>System test of SR-14 (d3), through the pages: a purchase invoice saved by an accountant raises the
/// stock of the chosen warehouse at once, and the items page shows it. A viewer cannot buy (SR-33), and a
/// warehouse of another company is refused (d5).</summary>
public class StockFlowTests : IClassFixture<StockiaFactory>
{
    private readonly StockiaFactory _factory;
    private readonly AccessSeeding.Data _data;
    private readonly int _supplierId;
    private readonly int _warehouseId;
    private readonly int _warehouseOfB;

    public StockFlowTests(StockiaFactory factory)
    {
        _factory = factory;
        _data = AccessSeeding.Ensure(factory, "stock");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockiaDbContext>();
        foreach (var role in Enum.GetValues<PostingAccount>())
        {
            var code = PostingAccountCodes.CodeOf(role);
            if (!db.Accounts.Any(a => a.CompanyId == _data.CompanyAId && a.Code == code))
                db.Accounts.Add(new Account { CompanyId = _data.CompanyAId, Code = code, Name = "Stock " + role, Type = "Asset" });
        }
        if (!db.Suppliers.Any(p => p.CompanyId == _data.CompanyAId && p.Name == "Stock supplier"))
            db.Suppliers.Add(new Supplier { CompanyId = _data.CompanyAId, Name = "Stock supplier" });
        if (!db.Warehouses.Any(w => w.CompanyId == _data.CompanyAId && w.Name == "Stock main"))
            db.Warehouses.Add(new Warehouse { CompanyId = _data.CompanyAId, Name = "Stock main" });
        if (!db.Warehouses.Any(w => w.CompanyId == _data.CompanyBId && w.Name == "Stock of B"))
            db.Warehouses.Add(new Warehouse { CompanyId = _data.CompanyBId, Name = "Stock of B" });
        db.SaveChanges();
        _supplierId = db.Suppliers.Single(p => p.CompanyId == _data.CompanyAId && p.Name == "Stock supplier").Id;
        _warehouseId = db.Warehouses.Single(w => w.CompanyId == _data.CompanyAId && w.Name == "Stock main").Id;
        _warehouseOfB = db.Warehouses.Single(w => w.CompanyId == _data.CompanyBId && w.Name == "Stock of B").Id;
    }

    /// <summary>A new stock item for each test, so the tests do not add to each other's stock.</summary>
    private int NewItem()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockiaDbContext>();
        var item = new Item { CompanyId = _data.CompanyAId, Code = "S" + Guid.NewGuid().ToString("N")[..8], Name = "Flour", Unit = "bag", VatRate = 11m, IsStock = true };
        db.Items.Add(item);
        db.SaveChanges();
        return item.Id;
    }

    private async Task<decimal> QuantityAsync(int itemId, int? warehouseId = null)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<StockService>().QuantityAsync(_data.CompanyAId, itemId, warehouseId);
    }

    private async Task<HttpClient> LoginAsync(string role)
    {
        var client = TestClient.Https(_factory);
        var response = await TestClient.LoginAsync(client, "stock-" + role, AccessSeeding.Password);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }

    private Dictionary<string, string> InvoiceForm(int itemId, int warehouseId) => new()
    {
        ["SupplierId"] = _supplierId.ToString(), ["Number"] = "SPI-" + Guid.NewGuid().ToString("N")[..8],
        ["Date"] = "2026-11-24", ["DueDate"] = "2026-12-24", ["WarehouseId"] = warehouseId.ToString(),
        ["Lines[0].ItemId"] = itemId.ToString(), ["Lines[0].Quantity"] = "10", ["Lines[0].UnitPrice"] = "20"
    };

    // BRD SR-14 acceptance: "Recording a purchase of 10 units raises stock by 10 immediately"
    [Fact]
    public async Task A_purchase_of_10_units_saved_on_the_page_raises_stock_by_10_and_the_items_page_shows_it()
    {
        var itemId = NewItem();
        var client = await LoginAsync("accountant");

        var saved = await TestClient.PostFormAsync(client, "/PurchaseInvoices/New", "/PurchaseInvoices/New", InvoiceForm(itemId, _warehouseId));

        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);
        Assert.Equal(10m, await QuantityAsync(itemId, _warehouseId));
        Assert.Contains($"id=\"stock-{itemId}\">10.00<", await client.GetStringAsync("/Items"));
        Assert.Contains("Stock main", await client.GetStringAsync(LoginRedirect.PathOf(saved)));
    }

    // SR-33: a viewer can look at the stock but cannot buy
    [Fact]
    public async Task A_viewer_cannot_save_a_purchase_and_the_stock_does_not_move()
    {
        var itemId = NewItem();
        var viewer = await LoginAsync("viewer");

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/Items")).StatusCode);
        var r = await TestClient.PostFormAsync(viewer, "/Accounts", "/PurchaseInvoices/New", InvoiceForm(itemId, _warehouseId));

        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal(0m, await QuantityAsync(itemId));
    }

    // d5: a warehouse of another company is refused, and nothing moves
    [Fact]
    public async Task A_warehouse_of_another_company_is_refused_on_the_page()
    {
        var itemId = NewItem();
        var client = await LoginAsync("accountant");

        var r = await TestClient.PostFormAsync(client, "/PurchaseInvoices/New", "/PurchaseInvoices/New", InvoiceForm(itemId, _warehouseOfB));

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("Choose a warehouse of this company", await r.Content.ReadAsStringAsync());
        Assert.Equal(0m, await QuantityAsync(itemId));
    }
}