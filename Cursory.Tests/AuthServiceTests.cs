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
    public void SetAllPasswords_forces_every_account_and_is_idempotent()
    {
        auth.SeedUser("gungreeneyes", "gungreeneyes", "Happygirl1005", UserRoles.Player, "#D85A30");
        auth.SeedUser("gideonkain",   "gideonkain",   "somethingElse9", UserRoles.Player, "#378ADD");

        var changed = auth.SetAllPasswords("800085");
        Assert.That(changed, Is.EqualTo(2), "both accounts should be re-hashed");
        Assert.That(auth.Authenticate("gungreeneyes", "800085"), Is.Not.Null);
        Assert.That(auth.Authenticate("gideonkain", "800085"), Is.Not.Null);
        Assert.That(auth.Authenticate("gungreeneyes", "Happygirl1005"), Is.Null, "old password revoked");

        // Second run is a no-op — everyone already on 800085.
        Assert.That(auth.SetAllPasswords("800085"), Is.EqualTo(0));
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
    public void CreateUser_rejects_invalid_role()
    {
        Assert.Throws<ArgumentException>(() =>
            auth.CreateUser("newuser", "New User", "Strong1!", "SuperAdmin", "#7F77DD"));
    }

    [Test]
    public void CreateUser_rejects_duplicate_username_case_insensitively()
    {
        auth.CreateUser("Taken", "First", "Strong1!", UserRoles.Player, "#7F77DD");
        Assert.Throws<InvalidOperationException>(() =>
            auth.CreateUser("TAKEN", "Second", "Strong2!", UserRoles.Player, "#7F77DD"));
        Assert.That(repo.Count, Is.EqualTo(1));
    }

    [Test]
    public void CreateUser_trims_username()
    {
        var user = auth.CreateUser("  spaced  ", "Spaced", "Strong1!", UserRoles.Player, "#7F77DD");
        Assert.That(user.Username, Is.EqualTo("spaced"));
    }

    [Test]
    public void CreateUser_sanitizes_display_name()
    {
        var user = auth.CreateUser("htmluser", "<b>Bold</b>  Name", "Strong1!", UserRoles.Player, "#7F77DD");
        Assert.That(user.DisplayName, Is.EqualTo("Bold Name"));
    }

    [Test]
    public void ChangePassword_hashes_new_password_rotates_stamp_and_clears_must_change()
    {
        var user = auth.CreateUser("changer", "Changer", "Strong1!", UserRoles.Player, "#7F77DD");
        var oldHash = user.PasswordHash;
        var oldStamp = user.SecurityStamp;
        user.MustChangePassword = true;
        repo.Update(user);

        auth.ChangePassword(user.Id, "Strongest2!");

        var updated = repo.GetById(user.Id)!;
        Assert.That(updated.PasswordHash, Is.Not.EqualTo(oldHash));
        Assert.That(updated.SecurityStamp, Is.Not.EqualTo(oldStamp));
        Assert.That(updated.MustChangePassword, Is.False);
        Assert.That(auth.Authenticate("changer", "Strongest2!"), Is.Not.Null);
        Assert.That(auth.Authenticate("changer", "Strong1!"), Is.Null);
    }

    [Test]
    public void ChangePassword_throws_for_unknown_user()
    {
        Assert.Throws<InvalidOperationException>(() =>
            auth.ChangePassword(Guid.NewGuid().ToString(), "Strongest2!"));
    }

    [Test]
    public void ChangePassword_throws_for_weak_password()
    {
        var user = auth.CreateUser("weakchanger", "Weak Changer", "Strong1!", UserRoles.Player, "#7F77DD");
        Assert.Throws<ArgumentException>(() => auth.ChangePassword(user.Id, "weak"));
    }

    [TestCase("")]
    [TestCase(null)]
    public void ChangePasswordWithVerification_rejects_empty_current_password(string? currentPassword)
    {
        var user = auth.CreateUser("verifier", "Verifier", "Strong1!", UserRoles.Player, "#7F77DD");
        Assert.Throws<ArgumentException>(() =>
            auth.ChangePasswordWithVerification(user.Id, currentPassword!, "Strongest2!"));
    }

    [Test]
    public void ChangePasswordWithVerification_rejects_wrong_current_password()
    {
        var user = auth.CreateUser("verifier2", "Verifier Two", "Strong1!", UserRoles.Player, "#7F77DD");
        Assert.Throws<ArgumentException>(() =>
            auth.ChangePasswordWithVerification(user.Id, "WrongPass1!", "Strongest2!"));
        Assert.That(auth.Authenticate("verifier2", "Strong1!"), Is.Not.Null, "password unchanged after rejection");
    }

    [Test]
    public void ChangePasswordWithVerification_succeeds_with_correct_current_password()
    {
        var user = auth.CreateUser("verifier3", "Verifier Three", "Strong1!", UserRoles.Player, "#7F77DD");
        auth.ChangePasswordWithVerification(user.Id, "Strong1!", "Strongest2!");
        Assert.That(auth.Authenticate("verifier3", "Strongest2!"), Is.Not.Null);
        Assert.That(auth.Authenticate("verifier3", "Strong1!"), Is.Null);
    }

    [TestCase(null, "Password is required.")]
    [TestCase("", "Password is required.")]
    [TestCase("Ab1!", "Password must be at least 8 characters.")]
    [TestCase("NoSpecial123", "Password must contain at least one special character: ~!@#$%^&*()-_=+'.,")]
    [TestCase("lowercase1!", "Password must contain at least one uppercase letter.")]
    [TestCase("UPPERCASE1!", "Password must contain at least one lowercase letter.")]
    [TestCase("NoDigitsHere!", "Password must contain at least one number.")]
    [TestCase("Aa1!\0Aa1", "Password contains invalid characters.")]
    [TestCase("Strong1!", null)]
    public void GetPasswordError_validates_each_rule(string? password, string? expected)
    {
        Assert.That(AuthService.GetPasswordError(password!), Is.EqualTo(expected));
    }

    [Test]
    public void GetPasswordError_rejects_password_over_bcrypt_limit()
    {
        var tooLong = "Aa1!" + new string('a', 69); // 73 chars total
        Assert.That(AuthService.GetPasswordError(tooLong),
            Is.EqualTo("Password must not exceed 72 characters (BCrypt limit)."));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void ValidateUsername_rejects_blank(string? username)
    {
        Assert.Throws<ArgumentException>(() => AuthService.ValidateUsername(username!));
    }

    [Test]
    public void ValidateUsername_rejects_too_long()
    {
        var tooLong = new string('a', 51);
        Assert.Throws<ArgumentException>(() => AuthService.ValidateUsername(tooLong));
    }

    [TestCase("ba d!")]
    [TestCase("ab")]
    public void ValidateUsername_rejects_bad_characters_or_under_minimum_length(string username)
    {
        Assert.Throws<ArgumentException>(() => AuthService.ValidateUsername(username));
    }

    [Test]
    public void ValidateUsername_accepts_three_character_minimum()
    {
        Assert.DoesNotThrow(() => AuthService.ValidateUsername("abc"));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void ValidateDisplayName_rejects_blank(string? displayName)
    {
        Assert.Throws<ArgumentException>(() => AuthService.ValidateDisplayName(displayName!));
    }

    [Test]
    public void ValidateDisplayName_rejects_too_long()
    {
        var tooLong = new string('a', 101);
        Assert.Throws<ArgumentException>(() => AuthService.ValidateDisplayName(tooLong));
    }

    [Test]
    public void ValidateDisplayName_rejects_nul_character()
    {
        Assert.Throws<ArgumentException>(() => AuthService.ValidateDisplayName("Bad\0Name"));
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
