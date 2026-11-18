using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Domain;

namespace Stockia.Web.Services;

/// <summary>The words used in the audit trail.</summary>
public static class AuditActions
{
    public const string EntryPosted = "entry posted";
    public const string EntryReversed = "entry reversed";
    public const string ReversalPosted = "reversal posted";
}

/// <summary>SR-6: records and reads the audit trail of one company. Every method takes the companyId
/// of the signed-in user (SR-33).</summary>
public class AuditService
{
    private readonly StockiaDbContext _db;
    private readonly TimeProvider _clock;
    public AuditService(StockiaDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task RecordAsync(int companyId, int userId, string action, string? details, int? entryId = null)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            CompanyId = companyId,
            UserId = userId,
            EntryId = entryId,
            LogTime = _clock.GetUtcNow().UtcDateTime,
            Action = action,
            Details = details is { Length: > 400 } ? details[..400] : details
        });
        await _db.SaveChangesAsync();
    }

    /// <summary>The newest lines first, for the audit page.</summary>
    public Task<List<AuditLog>> ListAsync(int companyId, int max = 200) =>
        _db.AuditLogs.AsNoTracking().Include(a => a.User)
            .Where(a => a.CompanyId == companyId)
            .OrderByDescending(a => a.LogTime).ThenByDescending(a => a.Id)
            .Take(max)
            .ToListAsync();

    /// <summary>The history of one entry, oldest first.</summary>
    public Task<List<AuditLog>> ForEntryAsync(int companyId, int entryId) =>
        _db.AuditLogs.AsNoTracking().Include(a => a.User)
            .Where(a => a.CompanyId == companyId && a.EntryId == entryId)
            .OrderBy(a => a.LogTime).ThenBy(a => a.Id)
            .ToListAsync();
}