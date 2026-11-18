using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Domain;

namespace Stockia.Web.Services;

public record NewJournalLine(int AccountId, decimal Debit, decimal Credit);

public record NewJournalEntry(DateTime Date, string? Reference, string? Description, IReadOnlyList<NewJournalLine> Lines);

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
    public JournalService(IJournalRepository repo) => _repo = repo;

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

        var entry = new JournalEntry
        {
            CompanyId = companyId,
            Date = input.Date.Date,
            Reference = reference,
            Description = description
        };

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
            entry.AddLine(line.AccountId, debit, credit);
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
