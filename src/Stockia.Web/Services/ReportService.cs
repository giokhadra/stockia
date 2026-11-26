using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;

namespace Stockia.Web.Services;

/// <summary>Item 22: what one account moved, or holds, over a span of dates. Debit and credit are totals of the
/// journal lines, in the base currency; Balance is debit minus credit (positive: a debit balance).</summary>
public record ReportLine(int AccountId, string Code, string Name, string Type, decimal Debit, decimal Credit)
{
    public decimal Balance => Debit - Credit;
}

/// <summary>Item 22: one line of the income statement or the balance sheet, already signed the natural way
/// (revenue, liabilities and equity as credit balances; assets and expenses as debit balances).</summary>
public record StatementLine(string Code, string Name, decimal Amount);

/// <summary>SR-21 / UC-12: the three statements of one company for a date range.
/// The trial balance and the income statement cover the movements from From to To; the balance sheet shows what
/// every account holds at To (all entries up to that date). Periods are not closed yet (SR-5), so the revenue minus
/// the expenses of all entries up to To stands in the balance sheet as "result not yet closed".</summary>
public record FinancialStatements(
    DateTime From, DateTime To,
    IReadOnlyList<ReportLine> TrialBalance,
    IReadOnlyList<StatementLine> Revenues, IReadOnlyList<StatementLine> Expenses,
    IReadOnlyList<StatementLine> Assets, IReadOnlyList<StatementLine> Liabilities, IReadOnlyList<StatementLine> Equity,
    decimal ResultToDate)
{
    public decimal TotalDebit => TrialBalance.Sum(l => l.Debit);
    public decimal TotalCredit => TrialBalance.Sum(l => l.Credit);
    /// <summary>BRD SR-21 acceptance: trial balance debits equal credits.</summary>
    public bool TrialBalanceBalances => TotalDebit == TotalCredit;

    public decimal TotalRevenue => Revenues.Sum(l => l.Amount);
    public decimal TotalExpenses => Expenses.Sum(l => l.Amount);
    public decimal NetIncome => TotalRevenue - TotalExpenses;

    public decimal TotalAssets => Assets.Sum(l => l.Amount);
    public decimal TotalLiabilities => Liabilities.Sum(l => l.Amount);
    public decimal TotalEquity => Equity.Sum(l => l.Amount);
    public decimal TotalLiabilitiesAndEquity => TotalLiabilities + TotalEquity + ResultToDate;
    /// <summary>BRD SR-21 acceptance: the balance sheet balances.</summary>
    public bool BalanceSheetBalances => TotalAssets == TotalLiabilitiesAndEquity;
}

/// <summary>SR-21 / UC-12: financial statements read from the journal of one company. Every method takes the
/// companyId of the signed-in user (SR-33). Nothing is stored: the statements are the sums of the journal lines.</summary>
public class ReportService
{
    private readonly StockiaDbContext _db;
    public ReportService(StockiaDbContext db) => _db = db;

    /// <summary>The three statements, or the reason the range is refused. A trial balance out of balance is not
    /// refused: the statements come back with TrialBalanceBalances false, so the page can show the error (UC-12 step 7).</summary>
    public async Task<(FinancialStatements? Statements, string? Error)> GenerateAsync(int companyId, DateTime? from, DateTime? to)
    {
        if (from is null || to is null) return (null, "Choose both dates.");
        if (from.Value.Date > to.Value.Date) return (null, "The start date cannot be after the end date.");

        var start = from.Value.Date;
        var end = to.Value.Date;
        var movements = await LinesAsync(companyId, start, end);   // the range: trial balance, income statement
        var holdings = await LinesAsync(companyId, null, end);      // everything up to the end: balance sheet

        List<StatementLine> Of(IEnumerable<ReportLine> lines, string type, bool creditSide) =>
            lines.Where(l => l.Type == type)
                 .Select(l => new StatementLine(l.Code, l.Name, creditSide ? -l.Balance : l.Balance))
                 .Where(l => l.Amount != 0)
                 .ToList();

        var resultToDate = -holdings.Where(l => l.Type is "Revenue" or "Expense").Sum(l => l.Balance);

        return (new FinancialStatements(start, end,
            movements,
            Of(movements, "Revenue", creditSide: true), Of(movements, "Expense", creditSide: false),
            Of(holdings, "Asset", creditSide: false), Of(holdings, "Liability", creditSide: true), Of(holdings, "Equity", creditSide: true),
            resultToDate), null);
    }

    /// <summary>Debit and credit totals per account of the company, for the entries dated from start (or from the
    /// beginning) to end. Accounts that did not move are left out; the list is in account-code order.</summary>
    private async Task<List<ReportLine>> LinesAsync(int companyId, DateTime? start, DateTime end)
    {
        var totals = await (
                from l in _db.JournalLines
                join e in _db.JournalEntries on l.JournalEntryId equals e.Id
                where e.CompanyId == companyId && l.CompanyId == companyId
                      && (start == null || e.Date >= start) && e.Date <= end
                group l by l.AccountId into g
                select new { AccountId = g.Key, Debit = g.Sum(x => x.Debit), Credit = g.Sum(x => x.Credit) })
            .ToListAsync();

        var ids = totals.Select(t => t.AccountId).ToList();
        var accounts = await _db.Accounts.Where(a => a.CompanyId == companyId && ids.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id);

        return totals.Where(t => accounts.ContainsKey(t.AccountId))
            .Select(t => new ReportLine(t.AccountId, accounts[t.AccountId].Code, accounts[t.AccountId].Name,
                accounts[t.AccountId].Type, t.Debit, t.Credit))
            .OrderBy(l => l.Code, StringComparer.Ordinal)
            .ToList();
    }
}