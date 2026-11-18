using Microsoft.EntityFrameworkCore;
using Stockia.Web.Data;
using Stockia.Web.Domain;

namespace Stockia.Web.Services;

public interface IUserRepository
{
    Task<AppUser?> FindByUsernameAsync(string username);
    Task UpdatePasswordHashAsync(AppUser user, string newHash);
}

public class EfUserRepository : IUserRepository
{
    private readonly StockiaDbContext _db;
    public EfUserRepository(StockiaDbContext db) => _db = db;

    public Task<AppUser?> FindByUsernameAsync(string username) =>
        _db.AppUsers.FirstOrDefaultAsync(u => u.Username == username);

    public async Task UpdatePasswordHashAsync(AppUser user, string newHash)
    {
        user.PasswordHash = newHash;
        await _db.SaveChangesAsync();
    }
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

    /// <summary>Returns the user when credentials are valid, otherwise null.
    /// SR-34: a valid password stored with an older hash is re-hashed with bcrypt on the spot.</summary>
    public async Task<AppUser?> ValidateAsync(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password)) return null;
        var user = await _users.FindByUsernameAsync(username.Trim());
        if (user is null || !_hasher.Verify(password, user.PasswordHash)) return null;

        if (_hasher.NeedsRehash(user.PasswordHash))
            await _users.UpdatePasswordHashAsync(user, _hasher.Hash(password));
        return user;
    }
}
