using Stockia.Web.Domain;
using Stockia.Web.Services;
using Xunit;

namespace Stockia.Tests;

/// <summary>Unit tests of item 4 (SR-34), task 4.1: bcrypt with work factor 12.</summary>
public class PasswordHasherTests
{
    private const string Password = "Admin#2026";
    private readonly BcryptPasswordHasher _hasher = new();

    [Fact]
    public void A_hash_is_bcrypt_with_work_factor_12()
    {
        Assert.Matches(@"^\$2[aby]\$12\$.{53}$", _hasher.Hash(Password));
    }

    [Fact]
    public void A_hash_never_contains_the_password()
    {
        Assert.DoesNotContain(Password, _hasher.Hash(Password));
    }

    [Fact]
    public void The_right_password_passes_and_a_wrong_one_fails()
    {
        var hash = _hasher.Hash(Password);
        Assert.True(_hasher.Verify(Password, hash));
        Assert.False(_hasher.Verify("admin#2026", hash));
    }

    [Fact]
    public void The_same_password_gives_two_different_hashes()
    {
        var first = _hasher.Hash(Password);
        var second = _hasher.Hash(Password);

        Assert.NotEqual(first, second);   // a new random salt every time
        Assert.True(_hasher.Verify(Password, first));
        Assert.True(_hasher.Verify(Password, second));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("$2b$12$tooshort")]
    [InlineData("100000.%%%.%%%")]
    [InlineData("100000..")]
    public void A_damaged_hash_is_refused_without_crashing(string stored)
    {
        Assert.False(_hasher.Verify(Password, stored));
    }

    [Fact]
    public void An_old_PBKDF2_hash_still_verifies_and_needs_rehash()
    {
        var old = new Pbkdf2PasswordHasher().Hash(Password);

        Assert.True(_hasher.Verify(Password, old));
        Assert.False(_hasher.Verify("wrong", old));
        Assert.True(_hasher.NeedsRehash(old));
    }

    [Fact]
    public void A_new_hash_does_not_need_rehash_but_a_weaker_bcrypt_hash_does()
    {
        Assert.False(_hasher.NeedsRehash(_hasher.Hash(Password)));
        Assert.True(_hasher.NeedsRehash(BCrypt.Net.BCrypt.HashPassword(Password, 10)));
    }
}

/// <summary>Task 4.1 "used by login": a valid login replaces an old hash with bcrypt.</summary>
public class LoginUpgradeTests
{
    private sealed class RecordingRepo : IUserRepository
    {
        public AppUser User { get; }
        public int Updates { get; private set; }
        public RecordingRepo(AppUser user) => User = user;

        public Task<AppUser?> FindByUsernameAsync(string username) =>
            Task.FromResult<AppUser?>(username == User.Username ? User : null);

        public Task UpdatePasswordHashAsync(AppUser user, string newHash)
        {
            user.PasswordHash = newHash;
            Updates++;
            return Task.CompletedTask;
        }
    }

    private static (LoginService Login, RecordingRepo Repo) Build(string storedHash)
    {
        var repo = new RecordingRepo(new AppUser { Id = 1, Username = "admin", DisplayName = "Administrator", PasswordHash = storedHash });
        return (new LoginService(repo, new BcryptPasswordHasher()), repo);
    }

    [Fact]
    public async Task A_valid_login_with_an_old_hash_upgrades_it_to_bcrypt()
    {
        var (login, repo) = Build(new Pbkdf2PasswordHasher().Hash("Admin#2026"));

        Assert.NotNull(await login.ValidateAsync("admin", "Admin#2026"));
        Assert.StartsWith("$2", repo.User.PasswordHash);
        Assert.Equal(1, repo.Updates);

        Assert.NotNull(await login.ValidateAsync("admin", "Admin#2026"));   // the new hash works
        Assert.Equal(1, repo.Updates);                                      // and is not replaced again
    }

    [Fact]
    public async Task A_wrong_password_changes_nothing()
    {
        var old = new Pbkdf2PasswordHasher().Hash("Admin#2026");
        var (login, repo) = Build(old);

        Assert.Null(await login.ValidateAsync("admin", "wrong"));
        Assert.Equal(old, repo.User.PasswordHash);
        Assert.Equal(0, repo.Updates);
    }
}
