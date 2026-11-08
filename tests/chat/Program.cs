static void Assert(bool ok, string message) { if (!ok) throw new Exception(message); }
// A transport failure must stop all later side effects, including browser launch.
for (int failedStage = 0; failedStage < 3; failedStage++)
{
    var stages = new List<int>();
    var progress = new List<string>();
    void Stage(int stage) { stages.Add(stage); if (stage == failedStage) throw new IOException("simulated failure"); }
    var flow = new ChatPreparation(_ => { Stage(0); return Task.CompletedTask; },
        _ => { Stage(1); return Task.CompletedTask; }, () => Stage(2), () => Stage(3), progress.Add);
    try { await flow.RunAsync(CancellationToken.None); throw new Exception("Failure reported as success"); }
    catch (IOException) { }
    Assert(stages.SequenceEqual(Enumerable.Range(0, failedStage + 1)), "Side effect continued after failure");
    Assert(!progress.Any(x => x.Contains("Запрос на открытие")), "Failed setup reported ready");
}
using (var cancelled = new CancellationTokenSource())
{
    int effects = 0;
    var flow = new ChatPreparation(_ => { effects++; cancelled.Cancel(); return Task.CompletedTask; },
        _ => { effects++; return Task.CompletedTask; }, () => effects++, () => effects++, _ => { });
    try { await flow.RunAsync(cancelled.Token); throw new Exception("Cancellation ignored"); }
    catch (OperationCanceledException) { }
    Assert(effects == 1, "Cancellation after LG switch did not stop headset/browser side effects");
}
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel(); int effects = 0;
    var flow = new ChatPreparation(_ => { effects++; return Task.CompletedTask; },
        _ => { effects++; return Task.CompletedTask; }, () => effects++, () => effects++, _ => { });
    try { await flow.RunAsync(cancelled.Token); throw new Exception("Pre-cancellation ignored"); }
    catch (OperationCanceledException) { }
    Assert(effects == 0, "Cancelled command touched TV or audio");
}
{
    var stages = new List<int>(); var messages = new List<string>();
    var flow = new ChatPreparation(_ => { stages.Add(0); return Task.CompletedTask; },
        _ => { stages.Add(1); return Task.CompletedTask; }, () => stages.Add(2), () => stages.Add(3), messages.Add);
    await flow.RunAsync(CancellationToken.None);
    Assert(stages.SequenceEqual(new[] { 0, 1, 2, 3 }), "Browser opened before audio was ready");
    Assert(messages.Last().Contains("нажмите кнопку голоса"), "Manual voice-start requirement omitted");
}
Console.WriteLine("Chat setup checks passed: failures stop later actions, cancellation boundaries, audio-before-browser, manual voice start.");

// Framing rejects truncated/oversized native messages before allocating payloads.
foreach (var bytes in new[] { new byte[] { 1, 0 }, BitConverter.GetBytes(VoiceFrames.Maximum + 1) })
{
    try { await VoiceFrames.ReadAsync(new MemoryStream(bytes), CancellationToken.None); throw new Exception("Invalid native frame accepted"); }
    catch (Exception ex) when (ex is IOException or InvalidDataException) { }
}
Assert(await VoiceFrames.ReadAsync(new MemoryStream(), CancellationToken.None) is null, "Clean EOF not handled");
using (var stream = new MemoryStream())
{
    await VoiceFrames.WriteAsync(stream, new byte[] { 1, 2, 3 }, CancellationToken.None);
    stream.Position = 0;
    Assert((await VoiceFrames.ReadAsync(stream, CancellationToken.None))!.SequenceEqual(new byte[] { 1, 2, 3 }), "Frame round trip failed");
}
// Exercise real named pipes without Chrome, user input, microphone or a network.
using (var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(12)))
{
    var bridge = new VoiceNativeBridge();
    var server = bridge.RunServerAsync(lifetime.Token);
    using var client = new System.IO.Pipes.NamedPipeClientStream(".", VoiceNativeBridge.PipeName,
        System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
    await client.ConnectAsync(lifetime.Token);
    while (!bridge.Connected) await Task.Delay(10, lifetime.Token);
    var result = bridge.SendAsync("start", lifetime.Token);
    using var request = System.Text.Json.JsonDocument.Parse((await VoiceFrames.ReadAsync(client, lifetime.Token))!);
    var id = request.RootElement.GetProperty("id").GetString();
    Assert(request.RootElement.GetProperty("action").GetString() == "start", "Wrong bridge action");
    await VoiceFrames.WriteAsync(client, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { id = "unknown", ok = true, code = "VOICE_UI_ACTIVE" }), lifetime.Token);
    Assert(!result.IsCompleted, "Wrong request ID completed voice start");
    await VoiceFrames.WriteAsync(client, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { id, ok = true, code = "VOICE_UI_ACTIVE" }), lifetime.Token);
    Assert(await result == "VOICE_UI_ACTIVE", "Voice result not delivered");
    using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
    var cancelResult = bridge.SendAsync("start", cancelled.Token);
    using var beforeCancel = System.Text.Json.JsonDocument.Parse((await VoiceFrames.ReadAsync(client, lifetime.Token))!);
    cancelled.Cancel();
    using var cancellation = System.Text.Json.JsonDocument.Parse((await VoiceFrames.ReadAsync(client, lifetime.Token))!);
    Assert(cancellation.RootElement.GetProperty("action").GetString() == "cancel", "Cancellation was not forwarded to Chrome");
    Assert(cancellation.RootElement.GetProperty("id").GetString() == beforeCancel.RootElement.GetProperty("id").GetString(), "Cancelled wrong request");
    try { await cancelResult; throw new Exception("Cancelled voice start completed"); } catch (OperationCanceledException) { }
    client.Dispose(); lifetime.Cancel(); await server;
}
Console.WriteLine("Native voice bridge checks passed: framing, request binding, real named-pipe round trip and cancellation.");
// Exercise the actual WinExe native-host entry point with redirected binary stdio.
if (OperatingSystem.IsWindows() && args.Length == 1)
{
    using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    var bridge = new VoiceNativeBridge();
    var server = bridge.RunServerAsync(lifetime.Token);
    using var host = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.GetFullPath(args[0])) {
        Arguments = "chrome-extension://" + VoiceExtensionIdentity.Id + "/", UseShellExecute = false,
        RedirectStandardInput = true, RedirectStandardOutput = true, CreateNoWindow = true
    }) ?? throw new Exception("Native host failed to launch");
    try
    {
        while (!bridge.Connected) await Task.Delay(20, lifetime.Token);
        var result = bridge.SendAsync("stop", lifetime.Token);
        using var request = System.Text.Json.JsonDocument.Parse((await VoiceFrames.ReadAsync(host.StandardOutput.BaseStream, lifetime.Token))!);
        var id = request.RootElement.GetProperty("id").GetString();
        Assert(request.RootElement.GetProperty("action").GetString() == "stop", "Native host changed command");
        await VoiceFrames.WriteAsync(host.StandardInput.BaseStream,
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new {id, ok = true, code = "VOICE_UI_STOPPED"}), lifetime.Token);
        Assert(await result == "VOICE_UI_STOPPED", "Native host response did not reach helper");
        host.StandardInput.Close();
        await host.WaitForExitAsync(lifetime.Token);
        Assert(host.ExitCode == 0, "Native host failed on clean EOF");
    }
    finally
    {
        if (!host.HasExited) host.Kill();
        lifetime.Cancel(); await server;
    }
    Console.WriteLine("Actual Windows native host binary stdio round trip passed.");
}
