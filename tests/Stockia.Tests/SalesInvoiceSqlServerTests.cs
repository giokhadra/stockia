using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Domain;
using Stockia.Web.Services;
using Xunit;

namespace Stockia.Tests;

/// <summary>Item 16 on the real database (CI, after db/013): customers and sales invoices are stored with their
/// Kind, and a sales invoice number is unique in the company (filtered index), whatever the customer.</summary>
[Collection("SqlServer")]
public class SalesInvoiceSqlServerTests
{
    private static StockiaDbContext Db() => new(new DbContextOptionsBuilder<StockiaDbContext>()
        .UseSqlServer(Environment.GetEnvironmentVariable("STOCKIA_TEST_CONNECTION")).Options);

    [SqlServerFact]
    public async Task A_sales_invoice_is_saved_posted_and_its_number_is_unique_in_the_company()
    {
        using var db = Db();
        var (company, error) = await new CompanyService(db).CreateAsync("SQL sales " + Guid.NewGuid().ToString("N")[..8], null, "USD");
        Assert.Null(error);
        foreach (var role in Enum.GetValues<PostingAccount>())
            db.Accounts.Add(new Account { CompanyId = company!.Id, Code = PostingAccountCodes.CodeOf(role), Name = role.ToString(), Type = "Asset" });
        var item = new Item { CompanyId = company!.Id, Code = "SV", Name = "Service", Unit = "h", Cost = 0m, SalePrice = 50m, VatRate = 11m, IsStock = false };
        db.Items.Add(item);
        var user = new AppUser { CompanyId = company.Id, Username = "sql-sales-" + Guid.NewGuid().ToString("N")[..8], PasswordHash = "x", DisplayName = "SQL" };
        db.AppUsers.Add(user);
        await db.SaveChangesAsync();

        var rates = new ExchangeRateService(db);
        var posting = new PostingService(new JournalService(new EfJournalRepository(db), rates), new EfPostedDocuments(db),
            new IPostingRule[] { new SalesInvoiceRule(), new PurchaseInvoiceRule() }, new AuditService(db, TimeProvider.System));
        var service = new SalesInvoiceService(db, posting, rates);
        var (c1, _) = await service.CreateCustomerAsync(company.Id, new CustomerForm("Customer one", null, null));
        var (c2, _) = await service.CreateCustomerAsync(company.Id, new CustomerForm("Customer two", null, null));

        var day = new DateTime(2026, 11, 26);
        var (invoice, errors) = await service.CreateAsync(company.Id, user.Id,
            new SalesInvoiceForm(c1!.Id, null, day, day.AddDays(30), null, null, new[] { new SalesLineForm(item.Id, 2m, null) }));
        Assert.Empty(errors);
        Assert.Equal(111.00m, invoice!.Total);

        db.ChangeTracker.Clear();
        Assert.Equal("Sales", await db.Database.SqlQueryRaw<string>(
            "SELECT Kind AS Value FROM dbo.Invoice WHERE Id = {0}", invoice.Id).SingleAsync());
        Assert.Equal("Customer", await db.Database.SqlQueryRaw<string>(
            "SELECT Kind AS Value FROM dbo.Partner WHERE Id = {0}", c1.Id).SingleAsync());
        Assert.Equal(111.00m, (await db.Partners.SingleAsync(p => p.Id == c1.Id)).Balance);

        // the database itself refuses the same number for another customer (db/013)
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(
            @"INSERT INTO dbo.Invoice (CompanyId, Kind, PartnerId, Number, Date, DueDate, Currency, Subtotal, VatTotal, Total, BaseTotal)
              VALUES ({0}, 'Sales', {1}, {2}, '2026-11-26', '2026-12-26', 'USD', 0, 0, 0, 0)", company.Id, c2!.Id, invoice.Number));
        Assert.Contains("UX_Invoice_SalesNumber", ex.Message);
    }
}
