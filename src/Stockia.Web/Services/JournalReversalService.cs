using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Domain;

namespace Stockia.Web.Services;

public record ReversalResult(bool Succeeded, int EntryId, string? Error)
{
    public static ReversalResult Ok(int entryId) => new(true, entryId, null);
    public static ReversalResult Fail(string error) => new(false, 0, error);
}

/// <summary>SR-6: a posted entry is never deleted or edited. A mistake is corrected by a reversal: a new
/// entry with every debit and credit swapped, linked to the original, with both recorded in the audit trail.
/// Every method takes the companyId of the signed-in user (SR-33).</summary>
public class JournalReversalService
{
    private readonly StockiaDbContext _db;
    private readonly TimeProvider _clock;
    public JournalReversalService(StockiaDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    /// <summary>The id of the entry that reverses this one, or null when it has not been reversed.</summary>
    public Task<int?> FindReversalIdAsync(int companyId, int entryId) =>
        _db.JournalEntries
            .Where(e => e.CompanyId == companyId && e.ReversesEntryId == entryId)
            .Select(e => (int?)e.Id)
            .FirstOrDefaultAsync();

    public async Task<ReversalResult> ReverseAsync(int companyId, int userId, int entryId, DateTime date)
    {
        var original = await _db.JournalEntries.Include(e => e.Lines)
            .FirstOrDefaultAsync(e => e.CompanyId == companyId && e.Id == entryId);
        if (original is null) return ReversalResult.Fail("Journal entry not found.");

        if (original.ReversesEntryId is not null)
            return ReversalResult.Fail("A reversal cannot be reversed. Post a new entry instead.");
        if (await FindReversalIdAsync(companyId, entryId) is not null)
            return ReversalResult.Fail("This entry has already been reversed.");

        var reference = "REV-" + original.Reference;
        if (reference.Length > 50) reference = reference[..50];

        var now = _clock.GetUtcNow().UtcDateTime;
        var reversal = new JournalEntry
        {
            CompanyId = companyId,
            Date = date.Date,
            Reference = reference,
            Description = "Reversal of " + original.Reference,
            ReversesEntryId = original.Id,
            CreatedAt = now
        };
        // Every debit becomes a credit and every credit a debit, on the same accounts. An account that has been
        // deactivated since is still allowed here: the reversal only undoes what was posted while it was active.
        foreach (var line in original.Lines.OrderBy(l => l.Id))
        {
            // SR-19: the base amounts are swapped as they are (no new conversion), and the currency, foreign
            // amount and rate are kept, so the reversal cancels the original exactly.
            var copy = reversal.AddLine(line.AccountId, line.Credit, line.Debit);
            copy.Currency = line.Currency;
            copy.ForeignAmount = line.ForeignAmount;
            copy.Rate = line.Rate;
        }

        _db.JournalEntries.Add(reversal);
        _db.AuditLogs.Add(new AuditLog
        {
            CompanyId = companyId, UserId = userId, EntryId = original.Id, LogTime = now,
            Action = AuditActions.EntryReversed, Details = "Reversed by " + reference
        });
        _db.AuditLogs.Add(new AuditLog
        {
            CompanyId = companyId, UserId = userId, Entry = reversal, LogTime = now,
            Action = AuditActions.ReversalPosted, Details = "Reverses " + original.Reference
        });

        try
        {
            await _db.SaveChangesAsync();   // the reversal and both audit lines are saved together
        }
        catch (DbUpdateException)
        {
            // two people reversed the same entry at the same moment: the table lets only one succeed
            return ReversalResult.Fail("This entry has already been reversed.");
        }
        return ReversalResult.Ok(reversal.Id);
    }
}