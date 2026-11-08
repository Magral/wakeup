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
