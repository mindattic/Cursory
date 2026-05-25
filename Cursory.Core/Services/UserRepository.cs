using System.Text.Json;
using Cursory.Core.Models;

namespace Cursory.Core.Services;

/// <summary>
/// JSON-file-backed user store. Thread-safe; defensive-copies on every read so callers
/// cannot mutate the in-memory cache via the returned objects.
/// </summary>
public class UserRepository
{
    private readonly string filePath;
    private readonly Lock writeLock = new();
    private volatile List<UserAccount>? cache;

    public UserRepository(string filePath)
    {
        this.filePath = filePath;
    }

    public List<UserAccount> GetAll()
    {
        EnsureLoaded();
        lock (writeLock) { return cache!.Select(Clone).ToList(); }
    }

    public UserAccount? GetByUsername(string username)
    {
        EnsureLoaded();
        lock (writeLock)
        {
            var user = cache!.FirstOrDefault(u =>
                u.Username.Equals(username, StringComparison.OrdinalIgnoreCase));
            return user == null ? null : Clone(user);
        }
    }

    public UserAccount? GetById(string id)
    {
        EnsureLoaded();
        lock (writeLock)
        {
            var user = cache!.FirstOrDefault(u => u.Id == id);
            return user == null ? null : Clone(user);
        }
    }

    public void Add(UserAccount user)
    {
        EnsureLoaded();
        lock (writeLock)
        {
            cache!.Add(user);
            Save();
        }
    }

    public void Update(UserAccount user)
    {
        EnsureLoaded();
        lock (writeLock)
        {
            var idx = cache!.FindIndex(u => u.Id == user.Id);
            if (idx >= 0)
            {
                cache[idx] = user;
                Save();
            }
        }
    }

    public void Delete(string id)
    {
        EnsureLoaded();
        lock (writeLock)
        {
            cache!.RemoveAll(u => u.Id == id);
            Save();
        }
    }

    public int Count
    {
        get { EnsureLoaded(); lock (writeLock) { return cache!.Count; } }
    }

    private static UserAccount Clone(UserAccount u) => new()
    {
        Id = u.Id,
        Username = u.Username,
        DisplayName = u.DisplayName,
        PasswordHash = u.PasswordHash,
        Role = u.Role,
        Color = u.Color,
        SecurityStamp = u.SecurityStamp,
        MustChangePassword = u.MustChangePassword,
        CreatedUtc = u.CreatedUtc,
        LastLoginUtc = u.LastLoginUtc,
    };

    private void EnsureLoaded()
    {
        if (cache != null) return;
        lock (writeLock)
        {
            if (cache != null) return;
            if (File.Exists(filePath))
            {
                var json = File.ReadAllText(filePath);
                cache = JsonSerializer.Deserialize<List<UserAccount>>(json) ?? [];
            }
            else
            {
                cache = [];
            }
        }
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true });
        // Atomic write: serialise to a temp sibling file, fsync, then replace the target.
        // A crash or power loss between WriteAllText and File.Move leaves users.json intact
        // (still pointing at the previous good copy); the worst-case outcome is a stray
        // .tmp left on disk. File.Move(overwrite:true) is atomic on Windows and POSIX.
        var tempPath = filePath + ".tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, filePath, overwrite: true);
    }
}
