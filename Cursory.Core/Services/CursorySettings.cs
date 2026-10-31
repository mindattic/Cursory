using Microsoft.Extensions.Configuration;
using MindAttic.Vault.Paths;
using MindAttic.Vault.Settings;
using MindAttic.Vault.Settings.Manifest;

namespace Cursory.Core.Services;

/// <summary>
/// Cursory's app-level settings in <c>%APPDATA%\MindAttic\Cursory\settings.json</c>, edited in
/// MindAttic.Vault.Desktop. The users themselves (users.json) are app data, managed in Cursory.
/// </summary>
[SettingsApp(AppId, "Cursory", Icon = "cursor", Description = "Shared-room multiplayer cursor game.")]
public sealed class CursorySettings
{
    public const string AppId = "Cursory";

    /// <summary>The configuration key the path was read from before LAMP; imported once.</summary>
    public const string LegacyUsersPathKey = "Cursory:UsersPath";

    [Setting("Users file", Group = "Storage", Order = 1,
        Help = "JSON file holding Cursory's accounts. Empty uses users.json in Cursory's MindAttic folder.")]
    [PathPicker(PathKind.File, Filter = "JSON|*.json|All files|*.*")]
    public string? UsersPath { get; set; }

    /// <summary>The users file in effect: <see cref="UsersPath"/>, or the default when empty.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string EffectiveUsersPath =>
        string.IsNullOrWhiteSpace(UsersPath) ? Path.Combine(VaultPaths.RoamingBucket(AppId), "users.json") : UsersPath;

    /// <summary>Loads the Vault settings, importing a still-configured <c>Cursory:UsersPath</c> on the first run.</summary>
    public static CursorySettings Load(IConfiguration? legacy = null, ISettingsStore<CursorySettings>? store = null)
    {
        // Deployed: values come by name from App Configuration (MindAttic:Settings:Cursory:*), never the disk.
        var deployed = legacy?.GetSection(SettingsManifest.PrefixFor(AppId));
        if (deployed is not null && deployed.Exists())
            return deployed.Get<CursorySettings>() ?? new CursorySettings();

        store ??= JsonSettingsStore<CursorySettings>.ForSettingsApp();
        var legacyPath = legacy?[LegacyUsersPathKey];
        if (!store.Exists() && !string.IsNullOrWhiteSpace(legacyPath))
            store.Save(new CursorySettings { UsersPath = legacyPath });
        return store.LoadOrSeed();
    }
}
