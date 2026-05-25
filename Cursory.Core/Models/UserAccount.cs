namespace Cursory.Core.Models;

public class UserAccount
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Username { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = UserRoles.Player;
    public string Color { get; set; } = "#7F77DD";
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString();
    public bool MustChangePassword { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginUtc { get; set; }
}

public static class UserRoles
{
    public const string Player = "Player";
    public const string Administrator = "Administrator";

    public static readonly string[] All = [Player, Administrator];
}
