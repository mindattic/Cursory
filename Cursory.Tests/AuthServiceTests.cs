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
        auth.SeedUser("gungreeneyes", "gungreeneyes", "Happygirl1005", UserRoles.Player, "#D85A30");
        Assert.That(repo.GetByUsername("gungreeneyes"), Is.Not.Null);
    }

    [Test]
    public void SeedUser_is_idempotent_on_unchanged_config()
    {
        auth.SeedUser("gungreeneyes", "gungreeneyes", "Happygirl1005", UserRoles.Player, "#D85A30");
        auth.SeedUser("gungreeneyes", "gungreeneyes", "Happygirl1005", UserRoles.Player, "#D85A30");
        Assert.That(repo.Count, Is.EqualTo(1));
        // Re-seeding with the same config is a no-op — the password still works.
        Assert.That(auth.Authenticate("gungreeneyes", "Happygirl1005"), Is.Not.Null);
    }

    [Test]
    public void SeedUser_migrates_a_rotated_password()
    {
        auth.SeedUser("gungreeneyes", "gungreeneyes", "Happygirl1005", UserRoles.Player, "#D85A30");
        // Operator rotates the seed password. On the next seed the stored hash is updated.
        auth.SeedUser("gungreeneyes", "gungreeneyes", "800085", UserRoles.Player, "#D85A30");
        Assert.That(repo.Count, Is.EqualTo(1));
        Assert.That(auth.Authenticate("gungreeneyes", "800085"), Is.Not.Null, "new password should work");
        Assert.That(auth.Authenticate("gungreeneyes", "Happygirl1005"), Is.Null, "old password should be revoked");
    }

    [Test]
    public void Authenticate_succeeds_with_seeded_password_that_violates_policy()
    {
        auth.SeedUser("gideonkain", "gideonkain", "Happygirl1005", UserRoles.Player, "#378ADD");
        var user = auth.Authenticate("gideonkain", "Happygirl1005");
        Assert.That(user, Is.Not.Null);
        Assert.That(user!.Username, Is.EqualTo("gideonkain"));
    }

    [Test]
    public void Authenticate_returns_null_on_wrong_password()
    {
        auth.SeedUser("gungreeneyes", "gungreeneyes", "Happygirl1005", UserRoles.Player, "#D85A30");
        Assert.That(auth.Authenticate("gungreeneyes", "wrong"), Is.Null);
    }

    [Test]
    public void Authenticate_returns_null_on_unknown_user()
    {
        Assert.That(auth.Authenticate("nobody", "anything"), Is.Null);
    }

    [Test]
    public void Authenticate_is_case_insensitive_on_username()
    {
        // Seeded lowercase; sign in via mixed case → still resolves.
        auth.SeedUser("gungreeneyes", "gungreeneyes", "Happygirl1005", UserRoles.Player, "#D85A30");
        Assert.That(auth.Authenticate("GunGreenEyes", "Happygirl1005"), Is.Not.Null);
    }

    [Test]
    public void SeedUser_normalises_existing_username_case()
    {
        // Pre-existing record from an earlier seed config has capitalised casing.
        auth.SeedUser("OldCapitalised", "OldCapitalised", "Happygirl1005", UserRoles.Player, "#D85A30");
        // Operator flips the seed config to lowercase. SeedUser finds the case-insensitive
        // match and migrates Username + DisplayName to the new canonical form, leaving
        // password and security stamp alone.
        auth.SeedUser("oldcapitalised", "oldcapitalised", "Happygirl1005", UserRoles.Player, "#D85A30");
        var record = repo.GetByUsername("oldcapitalised");
        Assert.That(record, Is.Not.Null);
        Assert.That(record!.Username, Is.EqualTo("oldcapitalised"));
        Assert.That(record.DisplayName, Is.EqualTo("oldcapitalised"));
        // Authenticate still works with the original password (no rehash).
        Assert.That(auth.Authenticate("oldcapitalised", "Happygirl1005"), Is.Not.Null);
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
