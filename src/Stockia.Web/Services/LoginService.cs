using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Domain;

namespace Stockia.Web.Services;

public interface IUserRepository
{
    Task<AppUser?> FindByUsernameAsync(string username);
}

public class EfUserRepository : IUserRepository
{
    private readonly StockiaDbContext _db;
    public EfUserRepository(StockiaDbContext db) => _db = db;

    public Task<AppUser?> FindByUsernameAsync(string username) =>
        _db.AppUsers.FirstOrDefaultAsync(u => u.Username == username);
}

public class LoginService
{
    private readonly IUserRepository _users;
    private readonly IPasswordHasher _hasher;

    public LoginService(IUserRepository users, IPasswordHasher hasher)
    {
        _users = users;
        _hasher = hasher;
    }

    /// <summary>Returns the user when credentials are valid, otherwise null.</summary>
    public async Task<AppUser?> ValidateAsync(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password)) return null;
        var user = await _users.FindByUsernameAsync(username.Trim());
        return user is not null && _hasher.Verify(password, user.PasswordHash) ? user : null;
    }
}
