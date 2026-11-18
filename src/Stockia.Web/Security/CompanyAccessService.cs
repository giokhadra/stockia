using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;

namespace Stockia.Web.Security;

public enum CompanyAccessResult
{
    Allowed,
    NoCompanyChosen,
    Forbidden
}

/// <summary>SR-33: one decision that every page and API asks: is this user assigned to the chosen
/// company, and does their role there allow this action? The role is read fresh on every request,
/// so a role change or a removed assignment takes effect immediately.</summary>
public class CompanyAccessService
{
    private readonly StockiaDbContext _db;
    public CompanyAccessService(StockiaDbContext db) => _db = db;

    public async Task<CompanyAccessResult> CheckAsync(
        int userId, int? companyId, IReadOnlyCollection<string> requiredRoles)
    {
        if (companyId is null) return CompanyAccessResult.NoCompanyChosen;

        var role = await (from a in _db.Assignments
                          join r in _db.Roles on a.RoleId equals r.Id
                          where a.UserId == userId && a.CompanyId == companyId.Value
                          select r.Name).FirstOrDefaultAsync();

        if (role is null) return CompanyAccessResult.Forbidden;          // another company's data
        if (requiredRoles.Count > 0 && !requiredRoles.Contains(role))
            return CompanyAccessResult.Forbidden;                        // role not allowed

        return CompanyAccessResult.Allowed;
    }
}
