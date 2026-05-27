using System.Text.RegularExpressions;
using Cursory.Core.Models;

namespace Cursory.Core.Services;

/// <summary>
/// BCrypt password hashing + login authentication. Modeled on StreetSamurai's AuthService:
/// constant-time response on missing users, per-account lockout, security-stamp invalidation
/// on role/password change. Adapted to username-based login (Cursory's two seeded accounts
/// are usernames, not emails).
/// </summary>
public class AuthService
{
    private readonly UserRepository users;

    private const int BcryptWorkFactor = 12;
    private const int MaxFailedAttempts = 10;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(5);

    public const int MinPasswordLength = 8;
    public const int MaxPasswordLength = 72;
    public const string SpecialChars = @"~!@#$%^&*()-_=+'.,";

    public const int MaxUsernameLength = 50;
    public const int MaxDisplayNameLength = 100;

    private readonly Dictionary<string, (int count, DateTime lastAttempt)> failedAttempts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock lockoutLock = new();

    private static readonly Regex UsernameRegex = new(
        @"^[A-Za-z0-9_-]{3,50}$",
        RegexOptions.Compiled);

    public AuthService(UserRepository users)
    {
        this.users = users;
    }

    public UserAccount? Authenticate(string username, string password)
    {
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return null;

        if (username.Contains('\0') || password.Contains('\0'))
            return null;

        var user = users.GetByUsername(username);

        // Constant-time BCrypt regardless of user existence or lockout state.
        var hashToVerify = user?.PasswordHash ?? "$2a$12$invalidhashpaddingtomatchlength00000000000000000000";
        var isValid = BCrypt.Net.BCrypt.Verify(password, hashToVerify);

        if (IsLockedOut(username))
            return null;

        if (user == null || !isValid)
        {
            RecordFailedAttempt(username);
            return null;
        }

        ClearFailedAttempts(username);
        user.LastLoginUtc = DateTime.UtcNow;
        users.Update(user);
        return user;
    }

    /// <summary>
    /// Seed a user, bypassing the password policy. Used only for the two predetermined
    /// seeded accounts whose passwords were given verbatim by the operator. Idempotent on
    /// unchanged config: if a user with this username (case-insensitive) already exists we
    /// leave the role alone and skip the write entirely when nothing changed. But the seed
    /// config IS authoritative for the canonical fields and the password — so flipping
    /// "GunGreenEyes" → "gungreeneyes", or rotating the seed password, migrates the stored
    /// record (re-hashing + invalidating the security stamp on a password change) on the next
    /// cold start, existing prod records included.
    /// </summary>
    public void SeedUser(string username, string displayName, string password, string role, string color)
    {
        ValidateUsername(username);
        ValidateDisplayName(displayName);
        ValidateRole(role);

        var existing = users.GetByUsername(username);
        if (existing != null)
        {
            var dirty = false;
            // Case migration: normalise the canonical fields when the seed config changes,
            // only writing if something actually moved so we don't churn users.json on boot.
            if (existing.Username != username || existing.DisplayName != displayName || existing.Color != color)
            {
                existing.Username = username;
                existing.DisplayName = displayName;
                existing.Color = color;
                dirty = true;
            }
            // Password migration: if the seed password no longer verifies against the stored
            // hash, the operator rotated it — re-hash and invalidate the security stamp so the
            // new password lands on the next cold start (and any live sessions are dropped).
            if (!BCrypt.Net.BCrypt.Verify(password, existing.PasswordHash))
            {
                existing.PasswordHash = BCrypt.Net.BCrypt.HashPassword(password, BcryptWorkFactor);
                existing.SecurityStamp = Guid.NewGuid().ToString();
                dirty = true;
            }
            if (dirty) users.Update(existing);
            return;
        }

        var user = new UserAccount
        {
            Username = username,
            DisplayName = displayName,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password, BcryptWorkFactor),
            Role = role,
            Color = color,
        };
        users.Add(user);
    }

    /// <summary>
    /// Force every stored account's password to <paramref name="password"/>, bypassing the
    /// password policy. Idempotent: an account whose hash already verifies is skipped, so on
    /// the steady state (everyone already on this password) it writes nothing. Re-hashed
    /// accounts get a fresh security stamp, dropping any live session. Returns the number of
    /// accounts changed. Operator action — there is no self-service signup, so this is the
    /// only way accounts get a password besides <see cref="SeedUser"/>.
    /// </summary>
    public int SetAllPasswords(string password)
    {
        var changed = 0;
        foreach (var user in users.GetAll())
        {
            if (BCrypt.Net.BCrypt.Verify(password, user.PasswordHash)) continue;
            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(password, BcryptWorkFactor);
            user.SecurityStamp = Guid.NewGuid().ToString();
            users.Update(user);
            changed++;
        }
        return changed;
    }

    public UserAccount CreateUser(string username, string displayName, string password, string role, string color)
    {
        ValidateUsername(username);
        ValidateDisplayName(displayName);
        ValidatePassword(password);
        ValidateRole(role);

        if (users.GetByUsername(username) != null)
            throw new InvalidOperationException($"User '{SanitizeForLog(username)}' already exists.");

        var user = new UserAccount
        {
            Username = username.Trim(),
            DisplayName = SanitizeDisplayName(displayName),
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password, BcryptWorkFactor),
            Role = role,
            Color = color,
        };
        users.Add(user);
        return user;
    }

    public void ChangePassword(string userId, string newPassword)
    {
        ValidatePassword(newPassword);
        var user = users.GetById(userId) ?? throw new InvalidOperationException("User not found.");
        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword, BcryptWorkFactor);
        user.SecurityStamp = Guid.NewGuid().ToString();
        user.MustChangePassword = false;
        users.Update(user);
    }

    public void ChangePasswordWithVerification(string userId, string currentPassword, string newPassword)
    {
        if (string.IsNullOrEmpty(currentPassword))
            throw new ArgumentException("Current password is required.");
        var user = users.GetById(userId) ?? throw new InvalidOperationException("User not found.");
        if (!BCrypt.Net.BCrypt.Verify(currentPassword, user.PasswordHash))
            throw new ArgumentException("Current password is incorrect.");
        ChangePassword(userId, newPassword);
    }

    public bool IsLockedOut(string username)
    {
        lock (lockoutLock)
        {
            if (!failedAttempts.TryGetValue(username, out var record)) return false;
            if (record.count >= MaxFailedAttempts)
            {
                if (DateTime.UtcNow - record.lastAttempt < LockoutDuration) return true;
                failedAttempts.Remove(username);
                return false;
            }
            return false;
        }
    }

    private const int MaxLockoutEntries = 10_000;
    private int recordsSinceCleanup;

    private void RecordFailedAttempt(string username)
    {
        lock (lockoutLock)
        {
            var current = failedAttempts.TryGetValue(username, out var record) ? record.count : 0;
            failedAttempts[username] = (current + 1, DateTime.UtcNow);

            recordsSinceCleanup++;
            if (recordsSinceCleanup >= 100 || failedAttempts.Count > MaxLockoutEntries)
            {
                EvictExpiredEntries();
                recordsSinceCleanup = 0;
            }
        }
    }

    private void EvictExpiredEntries()
    {
        var now = DateTime.UtcNow;
        var expired = failedAttempts
            .Where(kv => now - kv.Value.lastAttempt >= LockoutDuration)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var key in expired) failedAttempts.Remove(key);
    }

    private void ClearFailedAttempts(string username)
    {
        lock (lockoutLock) { failedAttempts.Remove(username); }
    }

    public static void ValidateUsername(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("Username is required.");
        if (username.Length > MaxUsernameLength)
            throw new ArgumentException($"Username must not exceed {MaxUsernameLength} characters.");
        if (!UsernameRegex.IsMatch(username.Trim()))
            throw new ArgumentException("Username must be 3-50 chars, letters/digits/underscore/dash only.");
    }

    public static void ValidatePassword(string password)
    {
        var error = GetPasswordError(password);
        if (error != null) throw new ArgumentException(error);
    }

    public static string? GetPasswordError(string password)
    {
        if (string.IsNullOrEmpty(password)) return "Password is required.";
        if (password.Length < MinPasswordLength) return $"Password must be at least {MinPasswordLength} characters.";
        if (password.Length > MaxPasswordLength) return $"Password must not exceed {MaxPasswordLength} characters (BCrypt limit).";
        if (password.Contains('\0')) return "Password contains invalid characters.";
        if (!password.Any(char.IsUpper)) return "Password must contain at least one uppercase letter.";
        if (!password.Any(char.IsLower)) return "Password must contain at least one lowercase letter.";
        if (!password.Any(char.IsDigit)) return "Password must contain at least one number.";
        if (!password.Any(c => SpecialChars.Contains(c)))
            return $"Password must contain at least one special character: {SpecialChars}";
        return null;
    }

    public static void ValidateDisplayName(string displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("Display name is required.");
        if (displayName.Length > MaxDisplayNameLength)
            throw new ArgumentException($"Display name must not exceed {MaxDisplayNameLength} characters.");
        if (displayName.Contains('\0'))
            throw new ArgumentException("Display name contains invalid characters.");
    }

    private static void ValidateRole(string role)
    {
        if (!UserRoles.All.Contains(role))
            throw new ArgumentException($"Invalid role '{role}'. Must be one of: {string.Join(", ", UserRoles.All)}");
    }

    public static string SanitizeDisplayName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name.Trim();
        var sanitized = Regex.Replace(name, @"<[^>]*>", "", RegexOptions.Compiled);
        sanitized = sanitized.Replace("\0", "");
        sanitized = Regex.Replace(sanitized.Trim(), @"\s+", " ");
        return sanitized;
    }

    public static string SanitizeForLog(string value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? "";
        return value.Replace("\r", "").Replace("\n", "").Replace("\0", "");
    }

    public static bool IsLocalUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (url.Any(char.IsControl)) return false;
        string decoded;
        try { decoded = Uri.UnescapeDataString(url); } catch { return false; }
        if (!decoded.StartsWith('/')) return false;
        if (decoded.StartsWith("//")) return false;
        if (decoded.StartsWith("/\\")) return false;
        var pathPortion = decoded;
        var queryIdx = decoded.IndexOf('?');
        var fragIdx = decoded.IndexOf('#');
        var delimIdx = (queryIdx >= 0, fragIdx >= 0) switch
        {
            (true, true) => Math.Min(queryIdx, fragIdx),
            (true, false) => queryIdx,
            (false, true) => fragIdx,
            _ => -1
        };
        if (delimIdx >= 0) pathPortion = decoded[..delimIdx];
        var afterSlash = pathPortion[1..];
        var colonIdx = afterSlash.IndexOf(':');
        var slashIdx = afterSlash.IndexOf('/');
        if (colonIdx >= 0 && (slashIdx < 0 || colonIdx < slashIdx)) return false;
        if (pathPortion.Contains('@')) return false;
        return true;
    }
}
