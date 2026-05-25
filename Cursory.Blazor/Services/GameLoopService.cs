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
    /// <summary>Threshold above which a tick logs a slow-tick warning. ~80% of the budget.</summary>
    private static readonly TimeSpan SlowTickThreshold = TimeSpan.FromMilliseconds(26);
    /// <summary>Drop cursors that haven't sent input in this many ticks (~5 s at 30 Hz).</summary>
    private const int StaleCursorTicks = 150;
    /// <summary>Run the stale-cursor eviction every N ticks (1 s).</summary>
    private const int EvictionEveryNTicks = 30;

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
        var slowTickLastLogged = DateTime.MinValue;
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var swStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    room.Step();
                    if (room.CurrentTick % EvictionEveryNTicks == 0)
                    {
                        var dropped = room.EvictStaleCursors(StaleCursorTicks);
                        if (dropped > 0) log.LogInformation("Evicted {N} stale cursor(s)", dropped);
                    }
                    var snap = room.Snapshot();
                    if (room.ConsumeGeometryRebroadcast())
                    {
                        // Level switch or full reset — push the fresh static geometry to every
                        // client before the next snapshot so walls/labels for the new level
                        // arrive ahead of the cursor/block state that references them.
                        await hub.Clients.All.SendAsync("Geometry", room.GeometryMessage(), stoppingToken);
                    }
                    var announceLevel = room.ConsumeLevelAnnouncement();
                    if (announceLevel > 0)
                    {
                        await hub.Clients.All.SendAsync("LevelLoaded", announceLevel, stoppingToken);
                    }
                    await hub.Clients.All.SendAsync("Snapshot", snap, stoppingToken);

                    // Slow-tick canary: alert operators when a tick crowds the 33 ms budget.
                    // Throttled to once per 5 s so a sustained slow-tick run doesn't drown the log.
                    var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(swStart);
                    if (elapsed > SlowTickThreshold && (DateTime.UtcNow - slowTickLastLogged) > TimeSpan.FromSeconds(5))
                    {
                        slowTickLastLogged = DateTime.UtcNow;
                        log.LogWarning(
                            "Slow tick: {Ms} ms (budget {BudgetMs} ms), cursors={Cursors}",
                            elapsed.TotalMilliseconds, TickInterval.TotalMilliseconds, room.AllCursors.Count);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    log.LogError(ex, "Game loop tick failed");
                }
            }
        }
        catch (OperationCanceledException) { /* host shutdown */ }
        log.LogInformation("Cursory game loop stopped");
    }
}
