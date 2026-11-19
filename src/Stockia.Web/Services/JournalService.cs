using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Domain;

namespace Stockia.Web.Services;

/// <summary>Debit and Credit are in <paramref name="Currency"/>; null, empty or the base currency means the
/// base currency (SR-19).</summary>
public record NewJournalLine(int AccountId, decimal Debit, decimal Credit, string? Currency = null);

/// <summary>SourceType and SourceId (SR-4) are set only by automatic posting: the document the entry comes from.</summary>
public record NewJournalEntry(DateTime Date, string? Reference, string? Description, IReadOnlyList<NewJournalLine> Lines,
    string? SourceType = null, int? SourceId = null);

/// <summary>Difference (SR-3) is set when the entry was refused because it is not balanced:
/// total debits minus total credits (positive: debits are higher).</summary>
public record PostResult(bool Succeeded, int EntryId, IReadOnlyList<string> Errors, decimal? Difference = null)
{
    public static PostResult Ok(int entryId) => new(true, entryId, Array.Empty<string>());
    public static PostResult Fail(IReadOnlyList<string> errors, decimal? difference = null) => new(false, 0, errors, difference);
}

public record AccountLedger(Account Account, IReadOnlyList<LedgerRow> Rows, decimal TotalDebit, decimal TotalCredit)
{
    /// <summary>Debit minus credit (a negative balance is a credit balance).</summary>
    public decimal Balance => TotalDebit - TotalCredit;
}

/// <summary>Every method takes the companyId of the signed-in user (SR-33) and filters by it.</summary>
public interface IJournalRepository
{
    Task<List<Account>> ListAccountsAsync(int companyId);
    Task<Account?> FindAccountAsync(int companyId, int accountId);
    Task AddAsync(JournalEntry entry);
    Task<JournalEntry?> FindEntryAsync(int companyId, int entryId);
    Task<List<JournalEntry>> EntriesTouchingAccountAsync(int companyId, int accountId);
}

public class EfJournalRepository : IJournalRepository
{
    private readonly StockiaDbContext _db;
    public EfJournalRepository(StockiaDbContext db) => _db = db;

    public Task<List<Account>> ListAccountsAsync(int companyId) =>
        _db.Accounts.AsNoTracking().Where(a => a.CompanyId == companyId).OrderBy(a => a.Code).ToListAsync();

    public Task<Account?> FindAccountAsync(int companyId, int accountId) =>
        _db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.CompanyId == companyId && a.Id == accountId);

    public async Task AddAsync(JournalEntry entry)
    {
        _db.JournalEntries.Add(entry);
        await _db.SaveChangesAsync();
    }

    public Task<JournalEntry?> FindEntryAsync(int companyId, int entryId) =>
        _db.JournalEntries.AsNoTracking()
            .Include(e => e.Lines).ThenInclude(l => l.Account)
            .FirstOrDefaultAsync(e => e.CompanyId == companyId && e.Id == entryId);

    public Task<List<JournalEntry>> EntriesTouchingAccountAsync(int companyId, int accountId) =>
        _db.JournalEntries.AsNoTracking()
            .Include(e => e.Lines)
            .Where(e => e.CompanyId == companyId && e.Lines.Any(l => l.AccountId == accountId))
            .ToListAsync();
}

public class JournalService
{
    private readonly IJournalRepository _repo;
    private readonly IExchangeRates? _rates;

    /// <summary><paramref name="rates"/> is needed only for lines in a foreign currency (SR-19).</summary>
    public JournalService(IJournalRepository repo, IExchangeRates? rates = null)
    {
        _repo = repo;
        _rates = rates;
    }

    /// <summary>All accounts of the company, active or not (a ledger can still be read after an account is deactivated).</summary>
    public Task<List<Account>> ListAccountsAsync(int companyId) => _repo.ListAccountsAsync(companyId);

    /// <summary>SR-1: only active accounts can be posted to, so only those are offered on the entry page.</summary>
    public async Task<List<Account>> ListPostableAccountsAsync(int companyId) =>
        (await _repo.ListAccountsAsync(companyId)).Where(a => a.IsActive).ToList();

    public Task<JournalEntry?> GetEntryAsync(int companyId, int entryId) => _repo.FindEntryAsync(companyId, entryId);

    /// <summary>The ledger of one account, or null when the account does not exist in this company.</summary>
    public async Task<AccountLedger?> GetLedgerAsync(int companyId, int accountId)
    {
        var account = await _repo.FindAccountAsync(companyId, accountId);
        if (account is null) return null;

        var entries = await _repo.EntriesTouchingAccountAsync(companyId, accountId);
        var rows = Ledger.Build(accountId, entries);
        return new AccountLedger(account, rows,
            Money.Total(rows.Select(r => r.Debit)), Money.Total(rows.Select(r => r.Credit)));
    }

    /// <summary>Validates and saves a manual journal entry for the company.</summary>
    public async Task<PostResult> PostAsync(int companyId, NewJournalEntry input)
    {
        var errors = new List<string>();
        var reference = input.Reference?.Trim() ?? "";
        var description = input.Description?.Trim() ?? "";

        if (input.Date == default) errors.Add("Date is required.");
        if (reference.Length == 0) errors.Add("Reference is required.");
        else if (reference.Length > 50) errors.Add("Reference can have at most 50 characters.");
        if (description.Length > 500) errors.Add("Description can have at most 500 characters.");
        if (input.Lines.Count < 2) errors.Add("An entry needs at least two lines.");
        if ((input.SourceType is null) != (input.SourceId is null))
            errors.Add("An entry linked to a document needs both the document type and the document id.");
        if (input.SourceType is { Length: > 30 }) errors.Add("The document type can have at most 30 characters.");

        var entry = new JournalEntry
        {
            CompanyId = companyId,
            Date = input.Date.Date,
            Reference = reference,
            Description = description,
            SourceType = input.SourceType,
            SourceId = input.SourceId
        };

        string? baseCurrency = null;
        for (var i = 0; i < input.Lines.Count; i++)
        {
            var line = input.Lines[i];
            var n = i + 1;
            var debit = Money.Round(line.Debit);
            var credit = Money.Round(line.Credit);

            if (debit < 0 || credit < 0 || (debit == 0) == (credit == 0))
            {
                errors.Add($"Line {n}: enter either a debit or a credit amount above zero, not both.");
                continue;
            }

            var account = await _repo.FindAccountAsync(companyId, line.AccountId);
            if (account is null)
            {
                errors.Add($"Line {n}: account not found.");
                continue;
            }
            if (!account.IsActive)
            {
                errors.Add($"Line {n}: account {account.Code} is inactive.");
                continue;
            }

            // SR-19: a line in a foreign currency is converted at the rate of the entry date and keeps both amounts.
            var currency = line.Currency?.Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(currency))
            {
                entry.AddLine(line.AccountId, debit, credit);
                continue;
            }
            if (!ExchangeRate.IsCurrencyCode(currency))
            {
                errors.Add($"Line {n}: the currency is a three-letter code, such as LBP or EUR.");
                continue;
            }
            if (_rates is null)
            {
                errors.Add($"Line {n}: amounts in another currency cannot be converted here.");
                continue;
            }
            baseCurrency ??= await _rates.BaseCurrencyAsync(companyId);
            if (currency == baseCurrency)
            {
                entry.AddLine(line.AccountId, debit, credit);
                continue;
            }
            var rate = await _rates.RateOnAsync(companyId, currency, input.Date);
            if (rate is null)
            {
                errors.Add($"Line {n}: there is no {currency} rate on or before " +
                           $"{input.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}. Add it on the Exchange rates page.");
                continue;
            }
            if (ExchangeRate.Convert(debit + credit, rate.Rate) == 0m)
            {
                errors.Add($"Line {n}: the amount is less than 0.01 {baseCurrency} once converted.");
                continue;
            }
            entry.AddForeignLine(line.AccountId, currency, debit, credit, rate.Rate);
        }

        // SR-3: refuse to post an entry whose total debits differ from its total credits, and say by how much.
        decimal? difference = null;
        if (errors.Count == 0 && !entry.IsBalanced)
        {
            difference = entry.Difference;
            errors.Add($"Debits ({entry.TotalDebit.ToString("0.00", CultureInfo.InvariantCulture)}) and credits " +
                       $"({entry.TotalCredit.ToString("0.00", CultureInfo.InvariantCulture)}) must be equal. " +
                       $"The difference is {Math.Abs(entry.Difference).ToString("0.00", CultureInfo.InvariantCulture)}.");
        }

        if (errors.Count > 0) return PostResult.Fail(errors, difference);

        await _repo.AddAsync(entry);
        return PostResult.Ok(entry.Id);
    }
}
