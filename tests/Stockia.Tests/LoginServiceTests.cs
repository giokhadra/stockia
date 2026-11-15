using Stockia.Web.Domain;
using Stockia.Web.Services;
using Xunit;

namespace Stockia.Tests;

public class LoginServiceTests
{
    private class FakeRepo : IUserRepository
    {
        private readonly AppUser _user;
        public FakeRepo(AppUser user) => _user = user;
        public Task<AppUser?> FindByUsernameAsync(string username) =>
            Task.FromResult<AppUser?>(username == _user.Username ? _user : null);
    }

    private static LoginService Build()
    {
        var hasher = new Pbkdf2PasswordHasher();
        var user = new AppUser { Id = 1, Username = "admin", DisplayName = "Administrator", PasswordHash = hasher.Hash("Admin#2026") };
        return new LoginService(new FakeRepo(user), hasher);
    }

    [Fact]
    public async Task Accepts_seeded_user() =>
        Assert.NotNull(await Build().ValidateAsync("admin", "Admin#2026"));

    [Fact]
    public async Task Refuses_wrong_password() =>
        Assert.Null(await Build().ValidateAsync("admin", "wrong"));

    [Fact]
    public async Task Refuses_unknown_username() =>
        Assert.Null(await Build().ValidateAsync("nobody", "Admin#2026"));
}
