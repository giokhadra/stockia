using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Domain;
using Stockia.Web.Services;
using Xunit;

namespace Stockia.Tests;

/// <summary>Item 22, SR-21 / UC-12 (d2, written first): the trial balance, income statement and balance sheet of a
/// company for a date range. BRD acceptance: trial balance debits equal credits; the balance sheet balances.</summary>
public class ReportTests
{
    private record Setup(ReportService Reports, StockiaDbContext Db, int A, int B, Dictionary<string, int> Accounts);

    /// <summary>Company A: capital 1000 (Jan), a sale of 200 + 22 VAT (Mar), a purchase of 80 (Mar), the customer
    /// pays 222 (Apr), and one older sale of 50 (Dec 2025). Company B has its own entry, never seen by A.</summary>
    private static async Task<Setup> Build()
    {
        var db = new StockiaDbContext(new DbContextOptionsBuilder<StockiaDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var a = new Company { Name = "Company A", BaseCurrency = "USD" };
        var b = new Company { Name = "Company B", BaseCurrency = "USD" };
        db.Companies.AddRange(a, b);
        await db.SaveChangesAsync();

        var accounts = new Dictionary<string, int>();
        foreach (var (code, name, type) in new[]
        {
            ("5121", "Bank", "Asset"), ("411", "Customers", "Asset"), ("401", "Suppliers", "Liability"),
            ("4457", "VAT collected", "Liability"), ("101", "Capital", "Equity"), ("701", "Sales", "Revenue"),
            ("601", "Purchases", "Expense"), ("622", "Rent", "Expense")
        })
        {
            var account = new Account { CompanyId = a.Id, Code = code, Name = name, Type = type };
            db.Accounts.Add(account);
            await db.SaveChangesAsync();
            accounts[code] = account.Id;
        }
        var bBank = new Account { CompanyId = b.Id, Code = "5121", Name = "B secret bank", Type = "Asset" };
        var bSales = new Account { CompanyId = b.Id, Code = "701", Name = "B secret sales", Type = "Revenue" };
        db.Accounts.AddRange(bBank, bSales);
        await db.SaveChangesAsync();

        var s = new Setup(new ReportService(db), db, a.Id, b.Id, accounts);
        await Entry(s, a.Id, new DateTime(2026, 1, 5), ("5121", 1000m, 0m), ("101", 0m, 1000m));
        await Entry(s, a.Id, new DateTime(2026, 3, 10), ("411", 222m, 0m), ("701", 0m, 200m), ("4457", 0m, 22m));
        await Entry(s, a.Id, new DateTime(2026, 3, 15), ("601", 80m, 0m), ("401", 0m, 80m));
        await Entry(s, a.Id, new DateTime(2026, 4, 2), ("5121", 222m, 0m), ("411", 0m, 222m));
        await Entry(s, a.Id, new DateTime(2025, 12, 20), ("5121", 50m, 0m), ("701", 0m, 50m));

        var other = new JournalEntry { CompanyId = b.Id, Date = new DateTime(2026, 3, 1), Reference = "B-1", Description = "B" };
        other.AddLine(bBank.Id, 999m, 0m);
        other.AddLine(bSales.Id, 0m, 999m);
        db.JournalEntries.Add(other);
        await db.SaveChangesAsync();
        return s;
    }

    private static async Task<JournalEntry> Entry(Setup s, int companyId, DateTime date, params (string Code, decimal Debit, decimal Credit)[] lines)
    {
        var entry = new JournalEntry { CompanyId = companyId, Date = date, Reference = "R" + date.ToString("MMdd"), Description = "test" };
        foreach (var (code, debit, credit) in lines) entry.AddLine(s.Accounts[code], debit, credit);
        s.Db.JournalEntries.Add(entry);
        await s.Db.SaveChangesAsync();
        return entry;
    }

    private static readonly DateTime Jan1 = new(2026, 1, 1), Dec31 = new(2026, 12, 31);

    private static async Task<FinancialStatements> Year(Setup s)
    {
        var (statements, error) = await s.Reports.GenerateAsync(s.A, Jan1, Dec31);
        Assert.Null(error);
        return statements!;
    }

    // ---- trial balance ----

    // BRD SR-21 acceptance: "Trial balance debits equal credits"
    [Fact]
    public async Task The_trial_balance_debits_equal_its_credits()
    {
        var tb = await Year(await Build());

        Assert.Equal(1524m, tb.TotalDebit);          // 1000 + 222 + 80 + 222
        Assert.Equal(1524m, tb.TotalCredit);
        Assert.True(tb.TrialBalanceBalances);
    }

    [Fact]
    public async Task The_trial_balance_shows_only_the_movements_of_the_range_in_account_order()
    {
        var tb = (await Year(await Build())).TrialBalance;

        var bank = tb.Single(l => l.Code == "5121");
        Assert.Equal(1222m, bank.Debit);              // the 50 of December 2025 is outside the range
        Assert.Equal(0m, bank.Credit);
        Assert.Equal(new[] { "101", "401", "411", "4457", "5121", "601", "701" }, tb.Select(l => l.Code));   // rent never moved
        Assert.Equal(0m, tb.Single(l => l.Code == "411").Balance);   // invoiced and paid
    }

    // UC-12 alternative: an unbalanced trial balance is shown with an error, not hidden
    [Fact]
    public async Task A_trial_balance_out_of_balance_is_reported()
    {
        var s = await Build();
        var broken = new JournalEntry { CompanyId = s.A, Date = new DateTime(2026, 6, 1), Reference = "BAD", Description = "one-sided" };
        broken.AddLine(s.Accounts["5121"], 10m, 0m);   // written straight to the table, past the SR-3 check
        s.Db.JournalEntries.Add(broken);
        await s.Db.SaveChangesAsync();

        var tb = await Year(s);

        Assert.False(tb.TrialBalanceBalances);
        Assert.Equal(10m, tb.TotalDebit - tb.TotalCredit);
    }

    // ---- income statement ----

    [Fact]
    public async Task The_income_statement_is_revenue_minus_expenses_of_the_range()
    {
        var year = await Year(await Build());

        Assert.Equal(200m, year.TotalRevenue);
        Assert.Equal(80m, year.TotalExpenses);
        Assert.Equal(120m, year.NetIncome);
        Assert.Equal("701", Assert.Single(year.Revenues).Code);
        Assert.Equal("601", Assert.Single(year.Expenses).Code);   // rent never moved, so it is not listed
    }

    [Fact]
    public async Task A_month_without_sales_shows_no_profit()
    {
        var s = await Build();
        var (april, _) = await s.Reports.GenerateAsync(s.A, new DateTime(2026, 4, 1), new DateTime(2026, 4, 30));

        Assert.Equal(0m, april!.NetIncome);           // the customer paying is not revenue
        Assert.True(april.TrialBalanceBalances);
    }

    [Fact]
    public async Task An_expense_bigger_than_revenue_gives_a_loss()
    {
        var s = await Build();
        await Entry(s, s.A, new DateTime(2026, 5, 1), ("622", 500m, 0m), ("5121", 0m, 500m));

        Assert.Equal(-380m, (await Year(s)).NetIncome);   // 200 - 80 - 500
    }

    [Fact]
    public async Task A_reversed_entry_leaves_no_trace_in_the_statements()
    {
        var s = await Build();
        await Entry(s, s.A, new DateTime(2026, 6, 1), ("411", 300m, 0m), ("701", 0m, 300m));
        await Entry(s, s.A, new DateTime(2026, 6, 2), ("411", 0m, 300m), ("701", 300m, 0m));   // its reversal (SR-6)

        var year = await Year(s);
        Assert.Equal(200m, year.TotalRevenue);
        Assert.True(year.TrialBalanceBalances);
        Assert.True(year.BalanceSheetBalances);
    }

    // ---- balance sheet ----

    // BRD SR-21 acceptance: "the balance sheet balances"
    [Fact]
    public async Task The_balance_sheet_balances_and_includes_everything_up_to_its_date()
    {
        var year = await Year(await Build());

        Assert.Equal(1272m, year.TotalAssets);        // bank 1000 + 222 + 50 (the older sale counts here)
        Assert.Equal(102m, year.TotalLiabilities);    // suppliers 80 + VAT 22
        Assert.Equal(1000m, year.TotalEquity);
        Assert.Equal(170m, year.ResultToDate);        // sales 200 + 50 - purchases 80
        Assert.Equal(1272m, year.TotalLiabilitiesAndEquity);
        Assert.True(year.BalanceSheetBalances);
        Assert.DoesNotContain(year.Assets, l => l.Code == "411");   // nothing left owed
    }

    [Fact]
    public async Task The_balance_sheet_at_an_earlier_date_balances_too()
    {
        var s = await Build();
        var (march, _) = await s.Reports.GenerateAsync(s.A, new DateTime(2026, 3, 1), new DateTime(2026, 3, 12));

        Assert.Equal(1272m, march!.TotalAssets);      // bank 1050 + customers 222
        Assert.Equal(222m, march.Assets.Single(l => l.Code == "411").Amount);
        Assert.Equal(22m, march.TotalLiabilities);    // the purchase of the 15th is not there yet
        Assert.Equal(250m, march.ResultToDate);
        Assert.True(march.BalanceSheetBalances);
    }

    // ---- refusals and company isolation (SR-33) ----

    [Fact]
    public async Task Another_companys_entries_never_appear()
    {
        var s = await Build();
        var year = await Year(s);

        Assert.DoesNotContain(year.TrialBalance, l => l.Name.StartsWith("B secret"));
        Assert.Equal(1524m, year.TotalDebit);

        var (ofB, _) = await s.Reports.GenerateAsync(s.B, Jan1, Dec31);
        Assert.Equal(999m, ofB!.TotalRevenue);
        Assert.DoesNotContain(ofB.TrialBalance, l => l.Name == "Bank");
    }

    [Fact]
    public async Task A_wrong_range_is_refused_with_its_reason()
    {
        var s = await Build();

        Assert.Equal("Choose both dates.", (await s.Reports.GenerateAsync(s.A, null, Dec31)).Error);
        Assert.Equal("The start date cannot be after the end date.", (await s.Reports.GenerateAsync(s.A, Dec31, Jan1)).Error);
        Assert.Null((await s.Reports.GenerateAsync(s.A, Jan1, Jan1)).Error);   // a single day is fine
    }

    [Fact]
    public async Task A_company_without_entries_gets_empty_statements_that_balance()
    {
        var s = await Build();
        var c = new Company { Name = "Empty", BaseCurrency = "USD" };
        s.Db.Companies.Add(c);
        await s.Db.SaveChangesAsync();

        var (empty, error) = await s.Reports.GenerateAsync(c.Id, Jan1, Dec31);

        Assert.Null(error);
        Assert.Empty(empty!.TrialBalance);
        Assert.True(empty.TrialBalanceBalances);
        Assert.True(empty.BalanceSheetBalances);
    }
}