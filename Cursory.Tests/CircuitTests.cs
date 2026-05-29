using Cursory.Core.Services;

namespace Cursory.Tests;

/// <summary>
/// Level 14's series-circuit evaluator: the bulb lights only when the wires form a closed loop
/// battery+ → resistor → bulb → battery− with both the resistor and bulb in series.
/// </summary>
[TestFixture]
public class CircuitTests
{
    private static RoomState BuildLevel14()
    {
        var room = new RoomState();
        room.AddOrUpdatePlayer("u1", "c1", "U1", "#fff");
        room.StartLevelVote("u1", 14);   // solo room → quorum 1, resolves at once
        Assert.That(room.CurrentLevel, Is.EqualTo(14));
        return room;
    }

    /// <summary>Grab a wire end, drag it onto a terminal, and release so it snaps.</summary>
    private static void Connect(RoomState room, string wireId, int end, string terminalId)
    {
        var t = room.Snapshot().Terminals.First(x => x.Id == terminalId);
        room.TryAttachWireEnd("u1", wireId, end, t.X, t.Y);
        var c = room.GetCursor("u1")!;
        c.X = t.X; c.Y = t.Y;
        room.Step();        // the held end follows the cursor onto the terminal
        room.Detach("u1");  // release → snaps to the terminal under it
    }

    [Test]
    public void Bulb_lights_on_complete_series_loop()
    {
        var room = BuildLevel14();
        // battery+ → resistor → bulb → battery−
        Connect(room, "w1", 0, "bat+");
        Connect(room, "w1", 1, "r-a");
        Connect(room, "w2", 0, "r-b");
        Connect(room, "w2", 1, "b-a");
        Connect(room, "w3", 0, "b-b");
        Connect(room, "w3", 1, "bat-");
        room.Step();

        Assert.That(room.GetComponent("bulb")!.Lit, Is.True, "complete series loop should light the bulb");
    }

    [Test]
    public void Bulb_stays_dark_with_a_gap_in_the_loop()
    {
        var room = BuildLevel14();
        // Everything but the last hop back to battery− — loop is open.
        Connect(room, "w1", 0, "bat+");
        Connect(room, "w1", 1, "r-a");
        Connect(room, "w2", 0, "r-b");
        Connect(room, "w2", 1, "b-a");
        Connect(room, "w3", 0, "b-b");
        // w3 end B left loose.
        room.Step();

        Assert.That(room.GetComponent("bulb")!.Lit, Is.False, "an open loop must not light the bulb");
    }

    [Test]
    public void Bulb_stays_dark_when_resistor_is_bypassed()
    {
        var room = BuildLevel14();
        // Wire the bulb straight across the battery, skipping the resistor — not a valid series loop.
        Connect(room, "w1", 0, "bat+");
        Connect(room, "w1", 1, "b-a");
        Connect(room, "w3", 0, "b-b");
        Connect(room, "w3", 1, "bat-");
        room.Step();

        Assert.That(room.GetComponent("bulb")!.Lit, Is.False, "resistor must be in series for the bulb to light");
    }
}
