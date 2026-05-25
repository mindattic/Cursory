using Cursory.Core.Models;
using Cursory.Core.Services;

namespace Cursory.Tests;

[TestFixture]
public class AuthServiceTests
{
    private string tempPath = null!;
    private UserRepository repo = null!;
    private AuthService auth = null!;

    [SetUp]
    public void SetUp()
    {
        tempPath = Path.Combine(Path.GetTempPath(), $"cursory-test-{Guid.NewGuid():N}.json");
        repo = new UserRepository(tempPath);
        auth = new AuthService(repo);
    }

    [TearDown]
    public void TearDown()
    {
        if (File.Exists(tempPath)) File.Delete(tempPath);
    }

    [Test]
    public void SeedUser_creates_account_on_first_call()
    {
        auth.SeedUser("GunGreenEyes", "GunGreenEyes", "Happygirl1005", UserRoles.Player, "#D85A30");
        Assert.That(repo.GetByUsername("GunGreenEyes"), Is.Not.Null);
    }

    [Test]
    public void SeedUser_is_idempotent()
    {
        auth.SeedUser("GunGreenEyes", "GunGreenEyes", "Happygirl1005", UserRoles.Player, "#D85A30");
        auth.SeedUser("GunGreenEyes", "GunGreenEyes", "different",     UserRoles.Player, "#D85A30");
        Assert.That(repo.Count, Is.EqualTo(1));
        // The second SeedUser must NOT overwrite the password.
        var user = auth.Authenticate("GunGreenEyes", "Happygirl1005");
        Assert.That(user, Is.Not.Null);
    }

    [Test]
    public void Authenticate_succeeds_with_seeded_password_that_violates_policy()
    {
        auth.SeedUser("GideonKain", "GideonKain", "Happygirl1005", UserRoles.Player, "#378ADD");
        var user = auth.Authenticate("GideonKain", "Happygirl1005");
        Assert.That(user, Is.Not.Null);
        Assert.That(user!.Username, Is.EqualTo("GideonKain"));
    }

    [Test]
    public void Authenticate_returns_null_on_wrong_password()
    {
        auth.SeedUser("GunGreenEyes", "GunGreenEyes", "Happygirl1005", UserRoles.Player, "#D85A30");
        Assert.That(auth.Authenticate("GunGreenEyes", "wrong"), Is.Null);
    }

    [Test]
    public void Authenticate_returns_null_on_unknown_user()
    {
        Assert.That(auth.Authenticate("nobody", "anything"), Is.Null);
    }

    [Test]
    public void Authenticate_is_case_insensitive_on_username()
    {
        auth.SeedUser("GunGreenEyes", "GunGreenEyes", "Happygirl1005", UserRoles.Player, "#D85A30");
        Assert.That(auth.Authenticate("gungreeneyes", "Happygirl1005"), Is.Not.Null);
    }

    [Test]
    public void CreateUser_rejects_weak_password()
    {
        Assert.Throws<ArgumentException>(() =>
            auth.CreateUser("newuser", "New User", "weakpass", UserRoles.Player, "#7F77DD"));
    }

    [Test]
    public void CreateUser_accepts_strong_password()
    {
        var user = auth.CreateUser("newuser", "New User", "Strong1!", UserRoles.Player, "#7F77DD");
        Assert.That(user, Is.Not.Null);
        Assert.That(auth.Authenticate("newuser", "Strong1!"), Is.Not.Null);
    }

    [Test]
    public void Lockout_triggers_after_ten_failures_and_blocks_correct_password()
    {
        auth.SeedUser("locked", "Locked", "Happygirl1005", UserRoles.Player, "#000000");
        for (var i = 0; i < 10; i++) auth.Authenticate("locked", "wrong");
        // 11th attempt with the right password is rejected because the account is locked.
        Assert.That(auth.IsLockedOut("locked"), Is.True);
        Assert.That(auth.Authenticate("locked", "Happygirl1005"), Is.Null);
    }

    [TestCase("/", true)]
    [TestCase("/foo", true)]
    [TestCase("/foo/bar?x=1", true)]
    [TestCase("//evil.com", false)]
    [TestCase("/\\evil.com", false)]
    [TestCase("javascript:alert(1)", false)]
    [TestCase("", false)]
    [TestCase("/foo@evil.com", false)]
    public void IsLocalUrl_filters_open_redirect_attempts(string url, bool expected)
    {
        Assert.That(AuthService.IsLocalUrl(url), Is.EqualTo(expected));
    }
}
