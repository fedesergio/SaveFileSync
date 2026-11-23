using SaveFileSync.Core.Model;
using SaveFileSync.Core.Service;
using SaveFileSync.Core.Sync;

namespace SaveFileSync.Simulation.Tests;

/// <summary>Phase 3 through <c>SyncService</c>: scheduled maintenance, restore commands, settings.</summary>
public sealed class HistoryServiceTests : IDisposable
{
    private const string Game = "testgame";
    private readonly Harness _h = new();
    private readonly TestPc _pc;
    private readonly string _unitId;

    public HistoryServiceTests()
    {
        _pc = _h.CreatePc("desktop");
        // A short quiet period keeps simulated play sessions well under the 10 minutes before the first cleanup.
        _pc.Config.Update(c => c.QuietPeriodSeconds = 10);
        _unitId = _pc.Service.AddUnitAsync(
            "Test Game", [new LocationDefinition { Id = "saves" }], [Game + ".exe"], _pc.Bindings).Await(_pc);
        _pc.Service.Tick();
    }

    public void Dispose() => _h.Dispose();

    private void Play(string content)
    {
        _pc.Write("slot1.sav", content);
        _pc.Service.NotifyLocalChange(_unitId);
        _pc.Clock.Advance(TimeSpan.FromSeconds(20)); // quiet period
        _pc.Service.Tick();
        Assert.Equal(UnitState.Synced, _pc.Status(_unitId).State);
    }

    [Fact]
    public void NewGames_GetTheConfiguredNumberOfVersionsToKeep()
    {
        _pc.Config.Update(c => c.DefaultKeepVersions = 4);
        var id = _pc.Service.AddUnitAsync("Other", [new LocationDefinition { Id = "saves2" }], [], new Dictionary<string, string> { ["saves2"] = Path.Combine(_h.Root, "x") }).Await(_pc);

        Assert.Equal(4, _pc.Remote.ReadUnitDefinition(id)!.EffectiveKeepVersions);
        Assert.Equal(10, _pc.Remote.ReadUnitDefinition(_unitId)!.EffectiveKeepVersions);
    }

    [Fact]
    public void KeepSetting_CanBeChanged_AndSurvivesOtherEdits()
    {
        _pc.Service.SetKeepVersionsAsync(_unitId, 3).Await(_pc);
        Assert.Equal(3, _pc.Remote.ReadUnitDefinition(_unitId)!.EffectiveKeepVersions);

        _pc.Service.UpdateUnitAsync(_pc.Service.GetDefinition(_unitId)!).Await(_pc);
        Assert.Equal(3, _pc.Remote.ReadUnitDefinition(_unitId)!.EffectiveKeepVersions);

        _pc.Service.SetKeepVersionsAsync(_unitId, 100000).Await(_pc);
        Assert.Equal(1000, _pc.Remote.ReadUnitDefinition(_unitId)!.EffectiveKeepVersions);
    }

    [Fact]
    public void Maintenance_RunsByItself_TenMinutesAfterStart_ThenDaily()
    {
        _pc.Service.SetKeepVersionsAsync(_unitId, 2).Await(_pc);
        for (var i = 1; i <= 6; i++)
        {
            Play($"v{i}");
        }

        // Not yet: the first run is 10 minutes after the service started ticking.
        Assert.Equal(0, _pc.Service.GetHistoryAsync(_unitId).Await(_pc).Count(e => e.Archived));

        _pc.Clock.Advance(TimeSpan.FromMinutes(11));
        _pc.Service.Tick();
        var history = _pc.Service.GetHistoryAsync(_unitId).Await(_pc);
        Assert.Equal(4, history.Count(e => e.Archived));
        Assert.Contains(_pc.Service.RecentActivity, a => a.Message.StartsWith("Archived 4", StringComparison.Ordinal));

        // The next run is a day later, not on every tick.
        Play("v7");
        _pc.Clock.Advance(TimeSpan.FromHours(23));
        _pc.Service.Tick();
        Assert.Equal(4, _pc.Service.GetHistoryAsync(_unitId).Await(_pc).Count(e => e.Archived));
        _pc.Clock.Advance(TimeSpan.FromHours(2));
        _pc.Service.Tick();
        Assert.Equal(5, _pc.Service.GetHistoryAsync(_unitId).Await(_pc).Count(e => e.Archived));
    }

    [Fact]
    public void Maintenance_CanBeSwitchedOff()
    {
        _pc.Config.Update(c => c.HistoryCleanup = false);
        _pc.Service.SetKeepVersionsAsync(_unitId, 1).Await(_pc);
        for (var i = 1; i <= 4; i++)
        {
            Play($"v{i}");
        }

        _pc.Clock.Advance(TimeSpan.FromDays(3));
        _pc.Service.Tick();

        Assert.Equal(0, _pc.Service.GetHistoryAsync(_unitId).Await(_pc).Count(e => e.Archived));
    }

    [Fact]
    public void RunMaintenanceNow_ReportsWhatItDid()
    {
        _pc.Service.SetKeepVersionsAsync(_unitId, 2).Await(_pc);
        for (var i = 1; i <= 5; i++)
        {
            Play($"v{i}");
        }

        var report = _pc.Service.RunMaintenanceAsync().Await(_pc);

        Assert.Equal(3, report.Archived);
        Assert.Null(report.Cleanup.SkippedReason);
        Assert.Equal(3, report.Cleanup.Marked);
    }

    [Fact]
    public void Restore_ThroughTheService_ChangesTheFiles_AndIsRefusedWhileTheGameRuns()
    {
        Play("v1");
        Play("v2");
        var v1 = _pc.Service.GetHistoryAsync(_unitId).Await(_pc).Last().VersionId;

        _pc.Processes.Running.Add(Game);
        _pc.Clock.Advance(TimeSpan.FromSeconds(6));
        _pc.Service.Tick();
        var refused = _pc.Service.RestoreVersionAsync(_unitId, v1).Await(_pc);
        Assert.Equal(SyncResultKind.PushDeferred, refused.Kind);
        Assert.Equal("v2", _pc.Read("slot1.sav"));

        _pc.Processes.Running.Clear();
        _pc.Clock.Advance(TimeSpan.FromSeconds(30));
        _pc.Service.Tick();
        var done = _pc.Service.RestoreVersionAsync(_unitId, v1).Await(_pc);

        Assert.Equal(SyncResultKind.Pulled, done.Kind);
        Assert.Equal("v1", _pc.Read("slot1.sav"));
        Assert.Contains(_pc.Service.RecentActivity, a => a.Message.Contains("Restored", StringComparison.Ordinal));
        Assert.Equal(UnitState.Synced, _pc.Status(_unitId).State);
    }

    [Fact]
    public void Pin_ThroughTheService_ShowsInTheHistory()
    {
        Play("v1");
        Play("v2");
        var oldest = _pc.Service.GetHistoryAsync(_unitId).Await(_pc).Last();

        _pc.Service.PinAsync(_unitId, oldest.VersionId, true).Await(_pc);

        Assert.True(_pc.Service.GetHistoryAsync(_unitId).Await(_pc).Single(e => e.VersionId == oldest.VersionId).PinnedHere);
    }

    [Fact]
    public void Devices_AreListed_AndAnotherPcCanBeRetired()
    {
        var ally = _h.CreatePc("ally");
        ally.Register();
        _pc.Service.Tick(); // registers the desktop through the service

        var devices = _pc.Service.GetDevicesAsync().Await(_pc);
        Assert.Equal(["desktop", "ally"], devices.Select(d => d.Info.Name));
        Assert.True(devices[0].IsThisPc);

        _pc.Service.RetireDeviceAsync(ally.Device.DeviceId).Await(_pc);

        Assert.Equal(SaveFileSync.Core.History.DeviceState.Retired, DeviceStateOf("ally"));
    }

    private SaveFileSync.Core.History.DeviceState DeviceStateOf(string name) =>
        _pc.Service.GetDevicesAsync().Await(_pc).Single(d => d.Info.Name == name).State;
}
