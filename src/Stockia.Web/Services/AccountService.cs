using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Domain;

namespace Stockia.Web.Services;

/// <summary>SR-1 / UC-1: the chart of accounts of one company.</summary>
public class AccountService
{
    public static readonly string[] Types = { "Asset", "Liability", "Equity", "Revenue", "Expense" };

    private readonly StockiaDbContext _db;
    public AccountService(StockiaDbContext db) => _db = db;

    public Task<List<Account>> ListAsync(int companyId) =>
        _db.Accounts.Where(a => a.CompanyId == companyId).OrderBy(a => a.Code).ToListAsync();

    public Task<Account?> FindAsync(int companyId, int id) =>
        _db.Accounts.FirstOrDefaultAsync(a => a.CompanyId == companyId && a.Id == id);

    public async Task<(Account? Account, string? Error)> CreateAsync(
        int companyId, string? code, string? name, string? type, int? parentId)
    {
        if (string.IsNullOrWhiteSpace(code)) return (null, "The account code is required.");
        code = code.Trim();
        if (await _db.Accounts.AnyAsync(a => a.CompanyId == companyId && a.Code == code))
            return (null, $"The account code {code} already exists in this company.");

        var error = await CheckAsync(companyId, null, name, type, parentId);
        if (error is not null) return (null, error);

        var account = new Account
        {
            CompanyId = companyId, Code = code, Name = name!.Trim(), Type = type!, ParentId = parentId
        };
        _db.Accounts.Add(account);
        await _db.SaveChangesAsync();
        return (account, null);
    }

    public async Task<(Account? Account, string? Error)> UpdateAsync(
        int companyId, int id, string? name, string? type, int? parentId)
    {
        var account = await FindAsync(companyId, id);
        if (account is null) return (null, "Account not found.");
        var error = await CheckAsync(companyId, id, name, type, parentId);
        if (error is not null) return (null, error);

        account.Name = name!.Trim();
        account.Type = type!;
        account.ParentId = parentId;
        await _db.SaveChangesAsync();
        return (account, null);
    }

    /// <summary>SR-1: accounts are deactivated, never deleted.</summary>
    public async Task<bool> SetActiveAsync(int companyId, int id, bool active)
    {
        var account = await FindAsync(companyId, id);
        if (account is null) return false;
        account.IsActive = active;
        await _db.SaveChangesAsync();
        return true;
    }

    private async Task<string?> CheckAsync(int companyId, int? selfId, string? name, string? type, int? parentId)
    {
        if (string.IsNullOrWhiteSpace(name)) return "The account name is required.";
        if (type is null || !Types.Contains(type)) return "Choose an account type from the list.";
        if (parentId is not null)
        {
            if (parentId == selfId) return "An account cannot be its own parent.";
            if (!await _db.Accounts.AnyAsync(a => a.Id == parentId && a.CompanyId == companyId))
                return "The parent account must belong to the same company.";
        }
        return null;
    }
}