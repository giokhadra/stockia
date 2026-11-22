using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Stockia.Web.Data;
using Stockia.Web.Domain;
using Stockia.Web.Services;
using Xunit;

namespace Stockia.Tests;

/// <summary>System test of SR-7 (d3): a purchase invoice saved on the page appears in the supplier's balance and in
/// accounts payable, each line takes its item's VAT rate; roles and companies are respected (SR-33).</summary>
public class PurchaseInvoiceFlowTests : IClassFixture<StockiaFactory>
{
    private readonly StockiaFactory _factory;
    private readonly AccessSeeding.Data _data;
    private readonly int _flourId;
    private readonly int _transportId;

    public PurchaseInvoiceFlowTests(StockiaFactory factory)
    {
        _factory = factory;
        _data = AccessSeeding.Ensure(factory, "purch");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockiaDbContext>();
        foreach (var role in Enum.GetValues<PostingAccount>())
        {
            var code = PostingAccountCodes.CodeOf(role);
            if (!db.Accounts.Any(a => a.CompanyId == _data.CompanyAId && a.Code == code))
                db.Accounts.Add(new Account { CompanyId = _data.CompanyAId, Code = code, Name = "Purch " + role, Type = "Asset" });
        }
        EnsureItem(db, "PF", "Flour", 11m, true);
        EnsureItem(db, "PT", "Transport", 0m, false);
         if (!db.Warehouses.Any(w => w.CompanyId == _data.CompanyAId))   // SR-14: stock lines need one
        db.Warehouses.Add(new Warehouse { CompanyId = _data.CompanyAId, Name = "Purch warehouse" });
        db.SaveChanges();
        _flourId = db.Items.Single(i => i.CompanyId == _data.CompanyAId && i.Code == "PF").Id;
        _transportId = db.Items.Single(i => i.CompanyId == _data.CompanyAId && i.Code == "PT").Id;
    }

    private void EnsureItem(StockiaDbContext db, string code, string name, decimal vat, bool stock)
    {
        if (!db.Items.Any(i => i.CompanyId == _data.CompanyAId && i.Code == code))
            db.Items.Add(new Item { CompanyId = _data.CompanyAId, Code = code, Name = name, Unit = "u", SalePrice = 1m, VatRate = vat, IsStock = stock });
    }

    private async Task<HttpClient> LoginAsync(string role)
    {
        var client = TestClient.Https(_factory);
        var response = await TestClient.LoginAsync(client, "purch-" + role, AccessSeeding.Password);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return client;
    }

    private async Task<int> SupplierAsync(HttpClient client, string name)
    {
        var r = await TestClient.PostFormAsync(client, "/Suppliers", "/Suppliers/Create",
            new Dictionary<string, string> { ["Name"] = name });
        Assert.Equal(HttpStatusCode.Redirect, r.StatusCode);
        using var scope = _factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<StockiaDbContext>()
            .Suppliers.Single(s => s.CompanyId == _data.CompanyAId && s.Name == name).Id;
    }

    private static string Unique(string p) => p + "-" + Guid.NewGuid().ToString("N")[..8];

    private Dictionary<string, string> InvoiceForm(int supplierId, string number) => new()
    {
        ["SupplierId"] = supplierId.ToString(), ["Number"] = number, ["Date"] = "2026-11-24", ["DueDate"] = "2026-12-24",
        ["Lines[0].ItemId"] = _flourId.ToString(), ["Lines[0].Quantity"] = "10", ["Lines[0].UnitPrice"] = "20",
        ["Lines[1].ItemId"] = _transportId.ToString(), ["Lines[1].Quantity"] = "1", ["Lines[1].UnitPrice"] = "30"
    };

    [Fact]
    public async Task A_saved_purchase_invoice_appears_in_the_suppliers_balance_and_in_accounts_payable()
    {
        var client = await LoginAsync("accountant");
        var name = Unique("Supplier");
        var supplierId = await SupplierAsync(client, name);
        var number = Unique("PI");

        var saved = await TestClient.PostFormAsync(client, "/PurchaseInvoices/New", "/PurchaseInvoices/New", InvoiceForm(supplierId, number));
        Assert.Equal(HttpStatusCode.Redirect, saved.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockiaDbContext>();
        var invoice = await db.PurchaseInvoices.Include(i => i.Lines).SingleAsync(i => i.Number == number);
        // flour 200 + 11% = 222 ; transport 30 + 0% = 30
        Assert.Equal(252m, invoice.Total);
        Assert.Equal(22m, invoice.VatTotal);
        Assert.Equal(11m, invoice.Lines.Single(l => l.ItemId == _flourId).VatRate);
        Assert.Equal(0m, invoice.Lines.Single(l => l.ItemId == _transportId).VatRate);

        // the supplier's balance, on its page
        Assert.Equal(252m, db.Suppliers.Single(s => s.Id == supplierId).Balance);
        Assert.Contains("252.00", await client.GetStringAsync("/Suppliers"));

        // accounts payable: the entry credits 401 with the total
        var payable = db.Accounts.Single(a => a.CompanyId == _data.CompanyAId && a.Code == PostingAccountCodes.CodeOf(PostingAccount.Payable));
        var entry = await db.JournalEntries.Include(e => e.Lines)
            .SingleAsync(e => e.SourceType == DocumentTypes.PurchaseInvoice && e.SourceId == invoice.Id);
        Assert.Equal(252m, entry.Lines.Single(l => l.AccountId == payable.Id).Credit);
        Assert.Contains(number, await client.GetStringAsync("/PurchaseInvoices"));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/PurchaseInvoices/Details/{invoice.Id}")).StatusCode);
    }

    [Fact]
    public async Task A_viewer_may_look_but_not_write()
    {
        var viewer = await LoginAsync("viewer");
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/Suppliers")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/PurchaseInvoices")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/PurchaseInvoices/New")).StatusCode);

        var r = await TestClient.PostFormAsync(viewer, "/Suppliers", "/Suppliers/Create",
            new Dictionary<string, string> { ["Name"] = Unique("Nope") });
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
    }

    [Fact]
    public async Task A_wrong_invoice_shows_its_errors_and_saves_nothing()
    {
        var client = await LoginAsync("accountant");
        var supplierId = await SupplierAsync(client, Unique("Supplier"));
        var form = InvoiceForm(supplierId, "");
        var r = await TestClient.PostFormAsync(client, "/PurchaseInvoices/New", "/PurchaseInvoices/New", form);

        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Contains("invoice number is required", await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Nobody_reaches_another_companys_invoice()
    {
        int foreignId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<StockiaDbContext>();
            var supplier = new Supplier { CompanyId = _data.CompanyBId, Name = Unique("B supplier") };
            db.Suppliers.Add(supplier);
            await db.SaveChangesAsync();
            var invoice = new PurchaseInvoice { CompanyId = _data.CompanyBId, PartnerId = supplier.Id, Number = Unique("B"), Date = DateTime.Today, DueDate = DateTime.Today, Currency = "USD" };
            db.PurchaseInvoices.Add(invoice);
            await db.SaveChangesAsync();
            foreignId = invoice.Id;
        }
        var client = await LoginAsync("accountant");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/PurchaseInvoices/Details/{foreignId}")).StatusCode);
    }
}
