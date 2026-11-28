using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Stockia.Web.Data;
using Stockia.Web.Domain;
using Stockia.Web.Services;
using Xunit;

namespace Stockia.Tests;

/// <summary>System test of item 20 (SR-11 / d3), through the pages: a receipt covering two open invoices marks
/// both as paid and brings the customer's balance to zero. Also d5: a viewer cannot record a receipt.</summary>
public class ReceiptFlowTests : IClassFixture<StockiaFactory>
{
    private readonly StockiaFactory _factory;
    private readonly AccessSeeding.Data _data;

    public ReceiptFlowTests(StockiaFactory factory)
    {
        _factory = factory;
        _data = AccessSeeding.Ensure(factory, "rc");
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<StockiaDbContext>();
        foreach (var role in Enum.GetValues<PostingAccount>())
            if (!db.Accounts.Any(a => a.CompanyId == _data.CompanyAId && a.Code == PostingAccountCodes.CodeOf(role)))
                db.Accounts.Add(new Account { CompanyId = _data.CompanyAId, Code = PostingAccountCodes.CodeOf(role), Name = role.ToString(), Type = "Asset" });
        if (!db.Items.Any(i => i.CompanyId == _data.CompanyAId && i.Code == "RC-SV"))
            db.Items.Add(new Item { CompanyId = _data.CompanyAId, Code = "RC-SV", Name = "Audit service", Unit = "h", Cost = 0m, SalePrice = 200m, VatRate = 11m, IsStock = false });
        db.SaveChanges();
    }

    /// <summary>A new customer with two posted invoices (222.00 and 111.00).</summary>
    private async Task<(int Customer, SalesInvoice First, SalesInvoice Second)> CustomerWithTwoInvoices()
    {
        using var scope = _factory.Services.CreateScope();
        var sales = scope.ServiceProvider.GetRequiredService<SalesInvoiceService>();
        var db = scope.ServiceProvider.GetRequiredService<StockiaDbContext>();
        var item = db.Items.Single(i => i.CompanyId == _data.CompanyAId && i.Code == "RC-SV").Id;
        var (customer, error) = await sales.CreateCustomerAsync(_data.CompanyAId, new CustomerForm("Customer " + Guid.NewGuid().ToString("N")[..8], null, null));
        Assert.Null(error);
        var day = new DateTime(2026, 11, 20);
        var (first, e1) = await sales.CreateAsync(_data.CompanyAId, _data.AccountantUserId,
            new SalesInvoiceForm(customer!.Id, null, day, day.AddDays(30), null, null, new[] { new SalesLineForm(item, 1m, null) }));
        var (second, e2) = await sales.CreateAsync(_data.CompanyAId, _data.AccountantUserId,
            new SalesInvoiceForm(customer.Id, null, day, day.AddDays(30), null, null, new[] { new SalesLineForm(item, 0.5m, null) }));
        Assert.Empty(e1.Concat(e2));
        return (customer.Id, first!, second!);
    }

    private async Task<HttpClient> LoginAsync(string role)
    {
        var client = TestClient.Https(_factory);
        Assert.Equal(HttpStatusCode.Redirect, (await TestClient.LoginAsync(client, "rc-" + role, AccessSeeding.Password)).StatusCode);
        return client;
    }

    [Fact]
    public async Task A_receipt_covering_two_open_invoices_marks_both_as_paid_and_brings_the_balance_to_zero()
    {
        var (customer, first, second) = await CustomerWithTwoInvoices();
        var client = await LoginAsync("accountant");

        var page = await client.GetStringAsync($"/Receipts/New?customerId={customer}");
        Assert.Contains(first.Number, page);
        Assert.Contains("222.00", page);                 // open amounts shown
        Assert.Contains("111.00", page);

        var post = await TestClient.PostFormAsync(client, $"/Receipts/New?customerId={customer}", "/Receipts/New", new Dictionary<string, string>
        {
            ["CustomerId"] = customer.ToString(), ["Date"] = "2026-11-28", ["Method"] = "Bank", ["Number"] = "", ["Description"] = "",
            ["Allocations[0].InvoiceId"] = first.Id.ToString(), ["Allocations[0].Amount"] = "222.00",
            ["Allocations[1].InvoiceId"] = second.Id.ToString(), ["Allocations[1].Amount"] = "111.00",
        });
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);

        var details = await client.GetStringAsync(LoginRedirect.PathOf(post));
        Assert.Contains("333.00", details);
        Assert.Contains(first.Number, details);
        Assert.Contains(second.Number, details);

        var after = await client.GetStringAsync($"/Receipts/New?customerId={customer}");
        Assert.Contains("No open invoices", after);       // both paid

        using var scope = _factory.Services.CreateScope();
        Assert.Equal(0.00m, scope.ServiceProvider.GetRequiredService<StockiaDbContext>().Partners.Single(p => p.Id == customer).Balance);
    }

    [Fact]
    public async Task A_receipt_beyond_the_open_amount_comes_back_with_the_reason()
    {
        var (customer, first, _) = await CustomerWithTwoInvoices();
        var client = await LoginAsync("accountant");

        var response = await TestClient.PostFormAsync(client, $"/Receipts/New?customerId={customer}", "/Receipts/New", new Dictionary<string, string>
        {
            ["CustomerId"] = customer.ToString(), ["Date"] = "2026-11-28", ["Method"] = "Cash",
            ["Allocations[0].InvoiceId"] = first.Id.ToString(), ["Allocations[0].Amount"] = "300",
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("only 222.00 is open", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_viewer_sees_the_receipts_but_cannot_record_one()
    {
        var (customer, first, _) = await CustomerWithTwoInvoices();
        var client = await LoginAsync("viewer");

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/Receipts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/Receipts/New?customerId={customer}")).StatusCode);
        var post = await TestClient.PostFormAsync(client, "/", "/Receipts/New", new Dictionary<string, string>
        {
            ["CustomerId"] = customer.ToString(), ["Date"] = "2026-11-28", ["Method"] = "Cash",
            ["Allocations[0].InvoiceId"] = first.Id.ToString(), ["Allocations[0].Amount"] = "10",
        });
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
    }
}
