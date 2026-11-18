using SaveFileSync.Core.Service;

namespace SaveFileSync.Simulation.Tests;

/// <summary>
/// PLAN.md §6.6 / §10.3: the Ally sleeps with the game open while the desktop pushes a
/// newer save. After resume the Ally must warn once, must not pull while the game runs,
/// and must either pull cleanly or raise a conflict when the game closes.
/// </summary>
public sealed class HandheldSleepTests : IDisposable
{
    private const string Game = "testgame";
    private readonly Harness _h = new();
    private readonly TestPc _desktop;
    private readonly TestPc _ally;
    private readonly string _unitId;

    public HandheldSleepTests()
    {
        _desktop = _h.CreatePc("desktop");
        _ally = _h.CreatePc("ally", TimeSpan.FromHours(-5));

        _desktop.Write("slot1.sav", "v1");
        _unitId = _desktop.Service.AddUnitAsync(
            "Test Game",
            [new Core.Model.LocationDefinition { Id = "saves" }],
            [Game + ".exe"],
            _desktop.Bindings).Await(_desktop);
        Tick(_desktop);

        Tick(_ally);
        Assert.Equal(UnitState.NotSetUp, _ally.Status(_unitId).State);
        _ally.Service.BindAsync(_unitId, _ally.Bindings).Await(_ally);
        Tick(_ally);
        Assert.Equal("v1", _ally.Read("slot1.sav"));
        Assert.Equal(UnitState.Synced, _ally.Status(_unitId).State);

        // The ally starts the game, then goes to sleep with it open.
        _ally.Processes.Running.Add(Game);
        Tick(_ally, TimeSpan.FromSeconds(6));
        Assert.Equal(UnitState.GameRunning, _ally.Status(_unitId).State);

        // Meanwhile the desktop plays and saves.
        _desktop.Write("slot1.sav", "v2-desktop");
        _desktop.Service.NotifyLocalChange(_unitId);
        Tick(_desktop, TimeSpan.FromSeconds(121));
        Assert.Equal(UnitState.Synced, _desktop.Status(_unitId).State);

        // The ally wakes up.
        _ally.Service.OnResume();
        Tick(_ally, TimeSpan.FromSeconds(1));
    }

    public void Dispose() => _h.Dispose();

    [Fact]
    public void AfterResume_WarnsOnce_AndDoesNotPullWhileGameRuns()
    {
        Assert.Equal(UnitState.Stale, _ally.Status(_unitId).State);
        Assert.Contains("desktop", _ally.Status(_unitId).Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Single(_ally.Notifications, n => n.Title.StartsWith("Newer save", StringComparison.Ordinal));
        Assert.Equal("v1", _ally.Read("slot1.sav"));

        // Further polls while still running: no repeated notice, still no pull.
        Tick(_ally, TimeSpan.FromSeconds(30));
        Tick(_ally, TimeSpan.FromMinutes(3));
        Assert.Single(_ally.Notifications, n => n.Title.StartsWith("Newer save", StringComparison.Ordinal));
        Assert.Equal("v1", _ally.Read("slot1.sav"));

        // A second sleep/resume cycle re-arms the notice.
        _ally.Service.OnResume();
        Tick(_ally, TimeSpan.FromSeconds(1));
        Assert.Equal(2, _ally.Notifications.Count(n => n.Title.StartsWith("Newer save", StringComparison.Ordinal)));
    }

    [Fact]
    public void GameClosedWithoutChanges_PullsWithoutConflict()
    {
        _ally.Processes.Running.Clear();
        Tick(_ally, TimeSpan.FromSeconds(6));
        Assert.Equal(UnitState.Waiting, _ally.Status(_unitId).State);
        Assert.Equal("v1", _ally.Read("slot1.sav"));

        Tick(_ally, TimeSpan.FromSeconds(16));
        Assert.Equal(UnitState.Synced, _ally.Status(_unitId).State);
        Assert.Equal("v2-desktop", _ally.Read("slot1.sav"));
        Assert.DoesNotContain(_ally.Notifications, n => n.Title.StartsWith("Conflict", StringComparison.Ordinal));
    }

    [Fact]
    public void GameWroteAfterResume_GivesConflict_WithBothVersionsKept()
    {
        _ally.Write("slot1.sav", "v2-ally");
        _ally.Service.NotifyLocalChange(_unitId);
        _ally.Processes.Running.Clear();
        Tick(_ally, TimeSpan.FromSeconds(6));
        Tick(_ally, TimeSpan.FromSeconds(16));

        var status = _ally.Status(_unitId);
        Assert.Equal(UnitState.Conflict, status.State);
        Assert.Single(_ally.Notifications, n => n.Title.StartsWith("Conflict", StringComparison.Ordinal));

        var candidates = _ally.Service.GetConflictCandidatesAsync(_unitId).Await(_ally);
        Assert.Equal(2, candidates.Count);
        var fromDesktop = candidates.Single(c => c.DeviceId == _desktop.Device.DeviceId);
        Assert.Contains(candidates, c => c.DeviceId == _ally.Device.DeviceId);
        Assert.True(_ally.Service.ArrivedWhileGameOpen(_unitId, fromDesktop.VersionId));
        Assert.Equal("v2-ally", _ally.Read("slot1.sav"));

        // Resolve in favour of the desktop.
        _ally.Service.ResolveConflictAsync(_unitId, fromDesktop.VersionId).Await(_ally);
        Assert.Equal("v2-desktop", _ally.Read("slot1.sav"));
        Assert.Equal(UnitState.Synced, _ally.Status(_unitId).State);
    }

    private static void Tick(TestPc pc, TimeSpan advance = default)
    {
        pc.Clock.Advance(advance);
        pc.Service.Tick();
    }
}

internal static class TaskTestExtensions
{
    /// <summary>Runs one scheduler tick (which executes queued commands) and returns the result.</summary>
    public static T Await<T>(this Task<T> task, TestPc pc)
    {
        pc.Service.Tick();
        Assert.True(task.IsCompleted, "Command was not executed by the tick.");
        return task.GetAwaiter().GetResult();
    }

    public static void Await(this Task task, TestPc pc)
    {
        pc.Service.Tick();
        Assert.True(task.IsCompleted, "Command was not executed by the tick.");
        task.GetAwaiter().GetResult();
    }
}
