// Pure sequencing: retries are permitted only after an observed update has
// completed. A missing window or slow launcher alone never causes another run.
internal sealed record SteamLaunchState(bool Running, bool Ready, bool Updating, string BuildId = "");
internal static class SteamLaunchFlow
{
    internal static async Task<string> MonitorAsync(Func<SteamLaunchState> inspect, Action launch,
        Action<string> progress, Func<CancellationToken, Task> pause, int maxSamples, CancellationToken token, SteamLaunchState? initial = null)
    {
        bool observedUpdate = initial?.Updating == true, retried = false;
        var initialBuild = initial?.BuildId ?? "";
        int readySamples = 0;
        for (var sample = 0; sample < maxSamples; sample++)
        {
            token.ThrowIfCancellationRequested();
            var state = inspect();
            if (state.Running) { progress("Игра запущена: Steam или процесс игры подтвердил запуск."); return "launch_requested"; }
            if (initialBuild.Length > 0 && state.BuildId.Length > 0 && initialBuild != state.BuildId) observedUpdate = true;
            if (state.Updating)
            {
                observedUpdate = true; readySamples = 0;
                progress("Steam обновляет игру. Жду завершения загрузки и установки…");
            }
            else if (observedUpdate && state.Ready && !retried)
            {
                if (++readySamples >= 3)
                {
                    // Steam may auto-launch after updating. Inspect again immediately
                    // before the one allowed retry to avoid launching a running app.
                    if (inspect().Running)
                    {
                        progress("Steam самостоятельно запустил игру после обновления.");
                        return "launch_requested";
                    }
                    launch(); retried = true;
                    progress("Обновление завершено. Повторный запрос запуска отправлен один раз.");
                }
            }
            else if (!state.Ready) readySamples = 0;
            await pause(token);
            if (!observedUpdate && sample >= 59)
            {
                progress("Запрос запуска отправлен, но запуск не подтверждён. Проверьте окно Steam: может требоваться вход, выбор режима или подтверждение.");
                return "launch_requested";
            }
        }
        progress(observedUpdate
            ? "Время ожидания истекло. Обновление или запуск игры не подтверждены; проверьте Steam. Автоматических повторов больше не будет."
            : "Запуск игры не подтверждён; проверьте Steam.");
        return "launch_requested";
    }
}
