using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

namespace Cursory.Core.Services;

/// <summary>
/// DI registration for the Cursory.Core slice. Hosts (Cursory.Blazor) call
/// services.AddCursoryCore(...). The users file comes from <see cref="CursorySettings"/> (edited in
/// MindAttic.Vault.Desktop); the default is %APPDATA%\MindAttic\Cursory\users.json.
/// </summary>
public static class CursoryServices
{
    public static IServiceCollection AddCursoryCore(this IServiceCollection services, IConfiguration config)
    {
        var settings = CursorySettings.Load(config);
        services.AddSingleton(settings);
        services.AddSingleton(_ => new UserRepository(settings.EffectiveUsersPath));
        services.AddSingleton<AuthService>();
        services.AddSingleton<RoomState>();
        return services;
    }
}
