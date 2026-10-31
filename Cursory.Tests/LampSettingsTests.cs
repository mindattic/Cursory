using Cursory.Core.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MindAttic.Vault.Paths;
using MindAttic.Vault.Settings;
using MindAttic.Vault.Settings.Manifest;
using NUnit.Framework;

namespace Cursory.Tests;

/// <summary>Every test in this assembly sees temp Vault roots, so nothing can write the real AppData.</summary>
[SetUpFixture]
public sealed class TestVaultRoots
{
    private string root = "";

    [OneTimeSetUp]
    public void SetUp()
    {
        root = Path.Combine(Path.GetTempPath(), "CursoryVault-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable(VaultPaths.RoamingRootEnvVar, Path.Combine(root, "roaming"));
        Environment.SetEnvironmentVariable(VaultPaths.LocalRootEnvVar, Path.Combine(root, "local"));
    }

    [OneTimeTearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable(VaultPaths.RoamingRootEnvVar, null);
        Environment.SetEnvironmentVariable(VaultPaths.LocalRootEnvVar, null);
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}

/// <summary>LAMP (CUR-T19): Cursory's settings in Vault, one end-to-end test per setting.</summary>
[NonParallelizable]
public class LampSettingsTests
{
    private static string Folder => VaultPaths.RoamingBucket(CursorySettings.AppId);

    [SetUp]
    public void SetUp()
    {
        if (Directory.Exists(Folder)) Directory.Delete(Folder, recursive: true);
    }

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Test]
    public void Setting_UsersPath_ImportsRoundTripsAndIsTheFileTheRepositoryUses()
    {
        var custom = Path.Combine(Path.GetTempPath(), "cursory-users-" + Guid.NewGuid().ToString("N") + ".json");
        Assert.That(CursorySettings.Load(Config(new() { ["Cursory:UsersPath"] = custom })).UsersPath, Is.EqualTo(custom), "old config key imported");
        Assert.That(CursorySettings.Load().UsersPath, Is.EqualTo(custom), "persisted in Vault");

        var registration = new ServiceCollection().AddCursoryCore(Config(new())).Single(d => d.ServiceType == typeof(UserRepository));
        var repo = (UserRepository)registration.ImplementationFactory!(null!);
        repo.Add(new Core.Models.UserAccount { Id = Guid.NewGuid().ToString(), Username = "u", DisplayName = "u", PasswordHash = "x", Role = Core.Models.UserRoles.Player, Color = "#000000" });
        Assert.That(File.Exists(custom), Is.True, "the repository writes the configured file");
        File.Delete(custom);

        JsonSettingsStore<CursorySettings>.ForSettingsApp().Update(s => s.UsersPath = "  ");
        Assert.That(CursorySettings.Load().EffectiveUsersPath, Is.EqualTo(Path.Combine(Folder, "users.json")), "blank uses the default");
    }

    [Test]
    public void EverySettingHasItsOwnTest()
    {
        var tested = GetType().GetMethods().Select(m => m.Name).Where(n => n.StartsWith("Setting_")).ToList();
        foreach (var field in SettingsManifestBuilder.Build<CursorySettings>().Fields)
            Assert.That(tested.Any(t => t.StartsWith("Setting_" + char.ToUpperInvariant(field.Property[0]) + field.Property[1..] + "_")), Is.True, field.Property);
    }

    [Test]
    public void DeployedConfiguration_WinsOverTheLocalFile()
    {
        JsonSettingsStore<CursorySettings>.ForSettingsApp().Save(new CursorySettings { UsersPath = "local.json" });
        var s = CursorySettings.Load(Config(new() { ["MindAttic:Settings:Cursory:usersPath"] = "/home/data/users.json" }));
        Assert.That(s.UsersPath, Is.EqualTo("/home/data/users.json"));
    }

    [Test]
    public void FreshMachine_SeedsTodaysDefaultAndTheManifest()
    {
        var s = CursorySettings.Load(Config(new()));
        Assert.That(s.UsersPath, Is.Null);
        Assert.That(s.EffectiveUsersPath, Is.EqualTo(Path.Combine(Folder, "users.json")), "same users.json as before LAMP");
        var m = SettingsManifestFile.TryRead(Path.Combine(Folder, SettingsManifest.FileName))!;
        Assert.That(m.Fields.Single().Kind, Is.EqualTo(SettingKind.Path));
        Assert.That(File.ReadAllText(Path.Combine(Folder, "settings.json")), Does.Not.Contain("effectiveUsersPath"));
    }

    [Test]
    public void ExistingVaultFile_IsNeverOverwrittenByTheOldKey()
    {
        JsonSettingsStore<CursorySettings>.ForSettingsApp().Save(new CursorySettings { UsersPath = "kept.json" });
        Assert.That(CursorySettings.Load(Config(new() { ["Cursory:UsersPath"] = "other.json" })).UsersPath, Is.EqualTo("kept.json"));
    }
}
