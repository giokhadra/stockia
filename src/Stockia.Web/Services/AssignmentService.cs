using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Domain;

namespace Stockia.Web.Services;

/// <summary>SR-20 / UC-15: assign users to companies with a role.</summary>
public class AssignmentService
{
    private readonly StockiaDbContext _db;
    public AssignmentService(StockiaDbContext db) => _db = db;

    public Task<List<Assignment>> ListAsync() =>
        _db.Assignments.Include(a => a.User).Include(a => a.Company).Include(a => a.Role)
            .OrderBy(a => a.User!.DisplayName).ThenBy(a => a.Company!.Name).ToListAsync();

    public Task<List<AppUser>> UsersAsync() => _db.AppUsers.OrderBy(u => u.DisplayName).ToListAsync();
    public Task<List<Company>> CompaniesAsync() => _db.Companies.OrderBy(c => c.Name).ToListAsync();
    public Task<List<Role>> RolesAsync() => _db.Roles.OrderBy(r => r.Id).ToListAsync();

    public Task<List<Company>> CompaniesOfAsync(int userId) =>
        _db.Assignments.Where(a => a.UserId == userId).Select(a => a.Company!).OrderBy(c => c.Name).ToListAsync();

    /// <summary>SR-33: every company the user may choose, with their role in each.</summary>
    public Task<List<Assignment>> ForUserAsync(int userId) =>
        _db.Assignments.Include(a => a.Company).Include(a => a.Role)
            .Where(a => a.UserId == userId).OrderBy(a => a.Company!.Name).ToListAsync();

    public Task<bool> IsAssignedAsync(int userId, int companyId) =>
        _db.Assignments.AnyAsync(a => a.UserId == userId && a.CompanyId == companyId);

    public async Task<(Assignment? Assignment, string? Error)> AssignAsync(int userId, int companyId, int roleId)
    {
        if (!await _db.AppUsers.AnyAsync(u => u.Id == userId)) return (null, "Choose an existing user.");
        if (!await _db.Companies.AnyAsync(c => c.Id == companyId)) return (null, "Choose an existing company.");
        if (!await _db.Roles.AnyAsync(r => r.Id == roleId)) return (null, "Choose a role from the list.");

        var existing = await _db.Assignments.FindAsync(userId, companyId);
        if (existing is not null)
        {
            existing.RoleId = roleId;          // already assigned: change the role
        }
        else
        {
            existing = new Assignment { UserId = userId, CompanyId = companyId, RoleId = roleId };
            _db.Assignments.Add(existing);
        }
        await _db.SaveChangesAsync();
        return (existing, null);
    }
}