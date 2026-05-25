using Cursory.Core.Models;
using Cursory.Core.Services;

namespace Cursory.Tests;

public class AuthServiceTests : IDisposable
{
    private readonly string tempPath;
    private readonly UserRepository repo;
    private readonly AuthService auth;

    public AuthServiceTests()
    {
        tempPath = Path.Combine(Path.GetTempPath(), $"cursory-test-{Guid.NewGuid():N}.json");
        repo = new UserRepository(tempPath);
        auth = new AuthService(repo);
    }

    public void Dispose()
    {
        if (File.Exists(tempPath)) File.Delete(tempPath);
    }

    [Fact]
    public void SeedUser_creates_account_on_first_call()
    {
        auth.SeedUser("GunGreenEyes", "GunGreenEyes", "Happygirl1005", UserRoles.Player, "#D85A30");
        Assert.NotNull(repo.GetByUsername("GunGreenEyes"));
    }

    [Fact]
    public void SeedUser_is_idempotent()
    {
        auth.SeedUser("GunGreenEyes", "GunGreenEyes", "Happygirl1005", UserRoles.Player, "#D85A30");
        auth.SeedUser("GunGreenEyes", "GunGreenEyes", "different",     UserRoles.Player, "#D85A30");
        Assert.Equal(1, repo.Count);
        // The second SeedUser must NOT overwrite the password.
        var user = auth.Authenticate("GunGreenEyes", "Happygirl1005");
        Assert.NotNull(user);
    }

    [Fact]
    public void Authenticate_succeeds_with_seeded_password_that_violates_policy()
    {
        auth.SeedUser("GideonKain", "GideonKain", "Happygirl1005", UserRoles.Player, "#378ADD");
        var user = auth.Authenticate("GideonKain", "Happygirl1005");
        Assert.NotNull(user);
        Assert.Equal("GideonKain", user!.Username);
    }

    [Fact]
    public void Authenticate_returns_null_on_wrong_password()
    {
        auth.SeedUser("GunGreenEyes", "GunGreenEyes", "Happygirl1005", UserRoles.Player, "#D85A30");
        Assert.Null(auth.Authenticate("GunGreenEyes", "wrong"));
    }

    [Fact]
    public void Authenticate_returns_null_on_unknown_user()
    {
        Assert.Null(auth.Authenticate("nobody", "anything"));
    }

    [Fact]
    public void Authenticate_is_case_insensitive_on_username()
    {
        auth.SeedUser("GunGreenEyes", "GunGreenEyes", "Happygirl1005", UserRoles.Player, "#D85A30");
        Assert.NotNull(auth.Authenticate("gungreeneyes", "Happygirl1005"));
    }

    [Fact]
    public void CreateUser_rejects_weak_password()
    {
        Assert.Throws<ArgumentException>(() =>
            auth.CreateUser("newuser", "New User", "weakpass", UserRoles.Player, "#7F77DD"));
    }

    [Fact]
    public void CreateUser_accepts_strong_password()
    {
        var user = auth.CreateUser("newuser", "New User", "Strong1!", UserRoles.Player, "#7F77DD");
        Assert.NotNull(user);
        Assert.NotNull(auth.Authenticate("newuser", "Strong1!"));
    }

    [Fact]
    public void Lockout_triggers_after_ten_failures_and_blocks_correct_password()
    {
        auth.SeedUser("locked", "Locked", "Happygirl1005", UserRoles.Player, "#000000");
        for (var i = 0; i < 10; i++) auth.Authenticate("locked", "wrong");
        // 11th attempt with the right password is rejected because the account is locked.
        Assert.True(auth.IsLockedOut("locked"));
        Assert.Null(auth.Authenticate("locked", "Happygirl1005"));
    }

    [Theory]
    [InlineData("/", true)]
    [InlineData("/foo", true)]
    [InlineData("/foo/bar?x=1", true)]
    [InlineData("//evil.com", false)]
    [InlineData("/\\evil.com", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("", false)]
    [InlineData("/foo@evil.com", false)]
    public void IsLocalUrl_filters_open_redirect_attempts(string url, bool expected)
    {
        Assert.Equal(expected, AuthService.IsLocalUrl(url));
    }
}
