using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Domain;
using Stockia.Web.Services;
using Xunit;

namespace Stockia.Tests;

public class AccountServiceTests
{
    private static async Task<(AccountService Service, StockiaDbContext Db, int A, int B)> Build()
    {
        var options = new DbContextOptionsBuilder<StockiaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var db = new StockiaDbContext(options);
        var a = new Company { Name = "Company A" };
        var b = new Company { Name = "Company B" };
        db.Companies.AddRange(a, b);
        await db.SaveChangesAsync();
        return (new AccountService(db), db, a.Id, b.Id);
    }

    [Fact]
    public async Task Creates_an_account_in_the_chart_of_the_company()
    {
        var (service, db, a, _) = await Build();
        var (account, error) = await service.CreateAsync(a, "411", "Customers", "Asset", null);

        Assert.Null(error);
        Assert.NotNull(account);
        var saved = await db.Accounts.SingleAsync();
        Assert.Equal(a, saved.CompanyId);
        Assert.Equal("411", saved.Code);
        Assert.True(saved.IsActive);
    }

    // BRD SR-1: a duplicate account code is rejected
    [Fact]
    public async Task Rejects_a_duplicate_code_in_the_same_company()
    {
        var (service, db, a, _) = await Build();
        await service.CreateAsync(a, "411", "Customers", "Asset", null);
        var (account, error) = await service.CreateAsync(a, "411", "Other", "Asset", null);

        Assert.Null(account);
        Assert.NotNull(error);
        Assert.Equal(1, await db.Accounts.CountAsync());
    }

    [Fact]
    public async Task Allows_the_same_code_in_another_company()
    {
        var (service, db, a, b) = await Build();
        await service.CreateAsync(a, "411", "Customers", "Asset", null);
        var (account, error) = await service.CreateAsync(b, "411", "Customers", "Asset", null);

        Assert.Null(error);
        Assert.NotNull(account);
        Assert.Equal(2, await db.Accounts.CountAsync());
    }

    [Fact]
    public async Task Lists_only_the_accounts_of_the_company()
    {
        var (service, _, a, b) = await Build();
        await service.CreateAsync(a, "411", "Customers", "Asset", null);
        await service.CreateAsync(b, "701", "Sales", "Revenue", null);

        var list = await service.ListAsync(a);
        Assert.Equal(new[] { "411" }, list.Select(x => x.Code));
    }

    [Fact]
    public async Task Refuses_a_parent_from_another_company()
    {
        var (service, db, a, b) = await Build();
        var (parentOfB, _) = await service.CreateAsync(b, "512", "Banks", "Asset", null);
        var (account, error) = await service.CreateAsync(a, "5121", "Bank Audi", "Asset", parentOfB!.Id);

        Assert.Null(account);
        Assert.NotNull(error);
    }

    [Fact]
    public async Task Refuses_an_empty_name_code_or_unknown_type()
    {
        var (service, db, a, _) = await Build();
        Assert.NotNull((await service.CreateAsync(a, "411", " ", "Asset", null)).Error);
        Assert.NotNull((await service.CreateAsync(a, " ", "Customers", "Asset", null)).Error);
        Assert.NotNull((await service.CreateAsync(a, "411", "Customers", "Banana", null)).Error);
        Assert.Empty(db.Accounts);
    }

    // BRD SR-1: deactivate, never delete
    [Fact]
    public async Task Deactivating_keeps_the_account_but_marks_it_inactive()
    {
        var (service, db, a, _) = await Build();
        var (account, _) = await service.CreateAsync(a, "411", "Customers", "Asset", null);

        Assert.True(await service.SetActiveAsync(a, account!.Id, false));
        Assert.False((await db.Accounts.SingleAsync()).IsActive);
        Assert.True(await service.SetActiveAsync(a, account.Id, true));
        Assert.True((await db.Accounts.SingleAsync()).IsActive);
    }

    [Fact]
    public async Task Edits_name_type_and_parent_but_not_the_code_and_refuses_self_as_parent()
    {
        var (service, db, a, _) = await Build();
        var (parent, _) = await service.CreateAsync(a, "512", "Banks", "Asset", null);
        var (child, _) = await service.CreateAsync(a, "5121", "Bank", "Asset", null);

        var (updated, error) = await service.UpdateAsync(a, child!.Id, "Bank Audi, USD", "Asset", parent!.Id);
        Assert.Null(error);
        Assert.Equal("Bank Audi, USD", updated!.Name);
        Assert.Equal(parent.Id, updated.ParentId);
        Assert.Equal("5121", updated.Code);

        Assert.NotNull((await service.UpdateAsync(a, child.Id, "X", "Asset", child.Id)).Error);
    }
}