static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
var sent = new List<int>();
using (var sync = new TvPowerCoordinator(() => true, (input, _) => { sent.Add(input); return Task.CompletedTask; }, () => {}, _ => {}))
{
    await sync.ResumeAsync();
    Assert(sent.SequenceEqual(new[]{1}), "Startup did not select HDMI1");
    await sync.ResumeAsync();
    Assert(sent.Count == 1, "Duplicate wake events must not reset a user's later manual input selection");
    await sync.PrepareDownAsync(TimeSpan.FromSeconds(1));
    Assert(sync.GoingDown && sent.SequenceEqual(new[]{1,2}), "Manual sleep/shutdown did not select HDMI2");
    await sync.PrepareDownAsync(TimeSpan.FromSeconds(1));
    Assert(sent.Count == 2, "Duplicate suspend notification sent twice");
    await sync.ResumeAsync();
    Assert(!sync.GoingDown && sent.SequenceEqual(new[]{1,2,1}), "Physical wake did not select HDMI1");
}
{
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var sequence = new List<int>();
    using var sync = new TvPowerCoordinator(() => true, async (input, token) => {
        if (input == 1) { started.TrySetResult(); await Task.Delay(Timeout.Infinite,token); }
        sequence.Add(input);
    }, () => {}, _ => {});
    var up = sync.ResumeAsync(); await started.Task;
    await sync.PrepareDownAsync(TimeSpan.FromSeconds(1)); await up;
    Assert(sequence.SequenceEqual(new[]{2}), "Stale delayed wake switched back after sleep");
}
{
    int calls = 0;
    using var sync = new TvPowerCoordinator(() => true, (_, _) => { if (++calls == 1) throw new IOException("TV offline"); return Task.CompletedTask; }, () => {}, _ => {});
    await sync.PrepareDownAsync(TimeSpan.FromSeconds(1));
    await sync.PrepareDownAsync(TimeSpan.FromSeconds(1));
    Assert(calls == 2, "Failed pre-sleep switch was not retried on real Windows suspend event");
}
{
    var watch = System.Diagnostics.Stopwatch.StartNew();
    using var sync = new TvPowerCoordinator(() => true, (_, token) => Task.Delay(Timeout.Infinite,token), () => {}, _ => {});
    await sync.PrepareDownAsync(TimeSpan.FromMilliseconds(80));
    Assert(watch.Elapsed < TimeSpan.FromSeconds(1), "TV failure delayed system sleep");
}
{
    bool enabled = false; int commands = 0;
    using var sync = new TvPowerCoordinator(() => enabled, (_, _) => { commands++; return Task.CompletedTask; }, () => {}, _ => {});
    await sync.ResumeAsync(); await sync.PrepareDownAsync(TimeSpan.FromSeconds(1));
    Assert(commands == 0, "Disabled/unpaired setup issued TV commands");
    enabled = true; await sync.ResumeAsync(); Assert(commands == 1, "Enabling synchronization did not allow wake");
}
Console.WriteLine("Power-to-HDMI sequencing, coalescing, cancellation, failures and deadlines passed.");

Assert(PowerEventPolicy.Decode(0x218,4,0) == TvPowerEvent.Suspend, "Windows suspend mapping");
Assert(PowerEventPolicy.Decode(0x218,18,0) == TvPowerEvent.Resume, "Automatic WoL wake mapping");
Assert(PowerEventPolicy.Decode(0x218,7,0) == TvPowerEvent.Resume, "Keyboard/power button wake mapping");
Assert(PowerEventPolicy.Decode(0x16,1,0) == TvPowerEvent.Shutdown, "Manual shutdown/restart mapping");
Assert(PowerEventPolicy.Decode(0x16,0,0) == TvPowerEvent.None, "Cancelled shutdown changed input");
Assert(PowerEventPolicy.Decode(0x16,1,0x80000000L) == TvPowerEvent.None, "Logoff changed input");
Assert(PowerEventPolicy.Decode(0x16,1,1) == TvPowerEvent.None, "Updating helper changed input");
Assert(PowerEventPolicy.Decode(0x11,0,0) == TvPowerEvent.None, "Unconfirmed shutdown query changed input");
Console.WriteLine("Native Windows event mapping excludes canceled shutdown, logoff and helper updates.");

var marker = Path.Combine(Path.GetTempPath(), "wake-session-" + Guid.NewGuid());
try
{
    Assert(!PowerStartupMarker.Record(marker, "session-1", false), "Installing first update unexpectedly stole TV input");
    Assert(!PowerStartupMarker.Record(marker, "session-1", true), "Helper relaunch in same Windows logon was mistaken for new boot");
    Assert(PowerStartupMarker.Record(marker, "session-2", false), "Next Windows logon with legacy shortcut not detected");
    Assert(!PowerStartupMarker.Record(marker, "session-2", false), "Updater relaunch switched TV to PC again");
    File.Delete(marker);
    Assert(PowerStartupMarker.Record(marker, "session-3", true), "First explicit Windows autostart did not select PC");
}
finally { if (File.Exists(marker)) File.Delete(marker); }
Console.WriteLine("Windows logon marker distinguishes actual startup from helper/update restarts.");

Assert(PowerEventPolicy.Decode(0x16,1,1,true) == TvPowerEvent.Shutdown, "Full Windows servicing shutdown must not be confused with helper-only update");
Assert(PowerEventPolicy.Decode(0x16,1,0x80000001L,true) == TvPowerEvent.None, "Logoff flag must stay excluded during servicing");
