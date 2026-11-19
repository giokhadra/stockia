using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Domain;
using Stockia.Web.Services;
using Xunit;

namespace Stockia.Tests;

/// <summary>SR-4 (d2): an entry can carry the document it comes from; a manual entry carries none.</summary>
public class JournalSourceLinkTests
{
    private static async Task<(JournalService Service, StockiaDbContext Db, int Company, int Cash, int Sales)> Build()
    {
        var options = new DbContextOptionsBuilder<StockiaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var db = new StockiaDbContext(options);
        var company = new Company { Name = "Company A" };
        db.Companies.Add(company);
        await db.SaveChangesAsync();

        var cash = new Account { CompanyId = company.Id, Code = "1000", Name = "Cash", Type = "Asset" };
        var sales = new Account { CompanyId = company.Id, Code = "4000", Name = "Sales", Type = "Revenue" };
        db.Accounts.AddRange(cash, sales);
        await db.SaveChangesAsync();
        return (new JournalService(new EfJournalRepository(db)), db, company.Id, cash.Id, sales.Id);
    }

    private static NewJournalEntry Entry(int cash, int sales, string? type, int? id) => new(
        new DateTime(2026, 11, 18), "SI-1", "Test", new[] { new NewJournalLine(cash, 10m, 0m), new NewJournalLine(sales, 0m, 10m) },
        type, id);

    [Fact]
    public async Task The_document_an_entry_comes_from_is_saved_with_it()
    {
        var (service, db, company, cash, sales) = await Build();
        var result = await service.PostAsync(company, Entry(cash, sales, DocumentTypes.SalesInvoice, 42));

        Assert.True(result.Succeeded, string.Join("; ", result.Errors));
        var saved = await db.JournalEntries.SingleAsync(e => e.Id == result.EntryId);
        Assert.Equal(DocumentTypes.SalesInvoice, saved.SourceType);
        Assert.Equal(42, saved.SourceId);
    }

    [Fact]
    public async Task A_manual_entry_has_no_source()
    {
        var (service, db, company, cash, sales) = await Build();
        var result = await service.PostAsync(company, Entry(cash, sales, null, null));

        Assert.True(result.Succeeded);
        var saved = await db.JournalEntries.SingleAsync(e => e.Id == result.EntryId);
        Assert.Null(saved.SourceType);
        Assert.Null(saved.SourceId);
    }

    [Theory]
    [InlineData("SalesInvoice", null)]
    [InlineData(null, 5)]
    public async Task A_source_needs_both_its_type_and_its_id(string? type, int? id)
    {
        var (service, db, company, cash, sales) = await Build();
        var result = await service.PostAsync(company, Entry(cash, sales, type, id));

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Contains("document type and the document id"));
        Assert.Empty(db.JournalEntries);
    }

    [Fact]
    public async Task A_document_type_longer_than_30_characters_is_refused()
    {
        var (service, db, company, cash, sales) = await Build();
        var result = await service.PostAsync(company, Entry(cash, sales, new string('T', 31), 1));

        Assert.False(result.Succeeded);
        Assert.Empty(db.JournalEntries);
    }
}
