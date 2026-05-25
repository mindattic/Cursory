using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

namespace Cursory.Core.Services;

/// <summary>
/// DI registration for the Cursory.Core slice. Hosts (Cursory.Blazor) call
/// services.AddCursoryCore(...). UserRepository file path is configurable via
/// the "Cursory:UsersPath" config key; the default is %APPDATA%\MindAttic\Cursory\users.json.
/// </summary>
public static class CursoryServices
{
    public static IServiceCollection AddCursoryCore(this IServiceCollection services, IConfiguration config)
    {
        var usersPath = config["Cursory:UsersPath"]
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MindAttic", "Cursory", "users.json");
        services.AddSingleton(_ => new UserRepository(usersPath));
        services.AddSingleton<AuthService>();
        services.AddSingleton<RoomState>();
        return services;
    }
}
