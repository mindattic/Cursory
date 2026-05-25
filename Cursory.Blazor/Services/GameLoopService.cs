using Microsoft.AspNetCore.SignalR;
using Cursory.Blazor.Hubs;
using Cursory.Core.Services;

namespace Cursory.Blazor.Services;

/// <summary>
/// Server-authoritative game loop. Ticks the physics on RoomState and broadcasts a
/// WorldSnapshot to every connected client at a fixed rate. Designed to scale to ~100
/// concurrent cursors per node on a single Azure App Service tier — the per-tick payload
/// is ~30 bytes × cursor count plus a handful of bytes per block/goal/whistle.
/// </summary>
public class GameLoopService : BackgroundService
{
    private const int TicksPerSecond = 30;
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(1000.0 / TicksPerSecond);

    private readonly RoomState room;
    private readonly IHubContext<RoomHub> hub;
    private readonly ILogger<GameLoopService> log;

    public GameLoopService(RoomState room, IHubContext<RoomHub> hub, ILogger<GameLoopService> log)
    {
        this.room = room;
        this.hub = hub;
        this.log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        log.LogInformation("Cursory game loop starting at {Tps} Hz", TicksPerSecond);
        using var timer = new PeriodicTimer(TickInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    room.Step();
                    var snap = room.Snapshot();
                    // SendAsync is fire-and-forget to the underlying transports; awaiting
                    // here serializes one snapshot send per tick which is the right backpressure.
                    await hub.Clients.All.SendAsync("Snapshot", snap, stoppingToken);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // A tick should never tear down the loop. Log and keep ticking.
                    log.LogError(ex, "Game loop tick failed");
                }
            }
        }
        catch (OperationCanceledException) { /* host shutdown */ }
        log.LogInformation("Cursory game loop stopped");
    }
}
