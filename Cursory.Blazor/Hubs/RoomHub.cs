using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Cursory.Core.Services;

namespace Cursory.Blazor.Hubs;

/// <summary>
/// The realtime room. Clients send their cursor positions and grab events; the server
/// updates RoomState and the GameLoopService broadcasts authoritative snapshots out.
/// Hub methods are write-only — they never return state to the caller. State arrives
/// over the snapshot channel instead.
/// </summary>
[Authorize]
public class RoomHub : Hub
{
    private readonly RoomState room;

    public RoomHub(RoomState room)
    {
        this.room = room;
    }

    public override async Task OnConnectedAsync()
    {
        var user = Context.User!;
        var userId = user.FindFirst("UserId")?.Value;
        if (string.IsNullOrEmpty(userId)) { Context.Abort(); return; }
        var name = user.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value ?? "Anon";
        var color = user.FindFirst("Color")?.Value ?? "#7F77DD";
        room.AddOrUpdatePlayer(userId, Context.ConnectionId, name, color);
        // One-shot static geometry — walls + labels — to this caller only. The 30 Hz
        // Snapshot stream carries only state that actually changes.
        await Clients.Caller.SendAsync("Geometry", room.GeometryMessage());
        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = Context.User?.FindFirst("UserId")?.Value;
        if (!string.IsNullOrEmpty(userId)) room.RemovePlayer(userId);
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>Client → server, 30Hz. World coordinates.</summary>
    public Task Move(double x, double y)
    {
        var user = Context.User;
        var userId = user?.FindFirst("UserId")?.Value;
        if (string.IsNullOrEmpty(userId)) return Task.CompletedTask;
        // A backgrounded tab throttles requestAnimationFrame, which stalls the 30 Hz input
        // stream long enough for the stale-cursor sweep to evict a player who is still
        // connected. When their Move resumes (tab refocused) the cursor is gone, so re-register
        // it from the connection's claims rather than silently dropping the input.
        if (!room.SetCursorPosition(userId, x, y))
        {
            var name = user!.FindFirst(System.Security.Claims.ClaimTypes.Name)?.Value ?? "Anon";
            var color = user.FindFirst("Color")?.Value ?? "#7F77DD";
            room.AddOrUpdatePlayer(userId, Context.ConnectionId, name, color);
            room.SetCursorPosition(userId, x, y);
        }
        return Task.CompletedTask;
    }

    /// <summary>Click on a block — attach this cursor at the click point.</summary>
    public Task Grab(string blockId, double x, double y)
    {
        var userId = Context.User?.FindFirst("UserId")?.Value;
        if (string.IsNullOrEmpty(userId)) return Task.CompletedTask;
        room.TryAttach(userId, blockId, x, y);
        return Task.CompletedTask;
    }

    /// <summary>Click on a compound rigid shape — attach this cursor at the click point.</summary>
    public Task GrabShape(string shapeId, double x, double y)
    {
        var userId = Context.User?.FindFirst("UserId")?.Value;
        if (string.IsNullOrEmpty(userId)) return Task.CompletedTask;
        room.TryAttachShape(userId, shapeId, x, y);
        return Task.CompletedTask;
    }

    /// <summary>Click on a static wall — anchor this cursor to its nearest edge.</summary>
    public Task GrabWall(string wallId, double x, double y)
    {
        var userId = Context.User?.FindFirst("UserId")?.Value;
        if (string.IsNullOrEmpty(userId)) return Task.CompletedTask;
        room.TryAttachWall(userId, wallId, x, y);
        return Task.CompletedTask;
    }

    /// <summary>Mouse up — detach this cursor.</summary>
    public Task Release()
    {
        var userId = Context.User?.FindFirst("UserId")?.Value;
        if (string.IsNullOrEmpty(userId)) return Task.CompletedTask;
        room.Detach(userId);
        return Task.CompletedTask;
    }

    /// <summary>Click in empty space — fire a whistle that others see + hear.</summary>
    public Task Whistle(double x, double y)
    {
        var userId = Context.User?.FindFirst("UserId")?.Value;
        if (string.IsNullOrEmpty(userId)) return Task.CompletedTask;
        room.RecordWhistle(userId, x, y);
        return Task.CompletedTask;
    }

    /// <summary>Kick off a vote to re-seed the current level.</summary>
    public Task StartResetVote()
    {
        var userId = Context.User?.FindFirst("UserId")?.Value;
        if (string.IsNullOrEmpty(userId)) return Task.CompletedTask;
        room.StartResetVote(userId);
        return Task.CompletedTask;
    }

    /// <summary>Kick off a vote to switch to a different level.</summary>
    public Task StartLevelVote(int targetLevel)
    {
        var userId = Context.User?.FindFirst("UserId")?.Value;
        if (string.IsNullOrEmpty(userId)) return Task.CompletedTask;
        room.StartLevelVote(userId, targetLevel);
        return Task.CompletedTask;
    }

    /// <summary>Cast yes/no on the active vote.</summary>
    public Task CastVote(bool yes)
    {
        var userId = Context.User?.FindFirst("UserId")?.Value;
        if (string.IsNullOrEmpty(userId)) return Task.CompletedTask;
        room.CastVote(userId, yes);
        return Task.CompletedTask;
    }
}
