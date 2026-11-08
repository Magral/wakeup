// Testable sequencing; Windows audio and TV transports are supplied by the host.
internal sealed class ChatPreparation(
    Func<CancellationToken, Task> releaseTv,
    Func<CancellationToken, Task> connectHeadset,
    Action selectAudio,
    Func<CancellationToken, Task> openChat,
    Action<string> progress,
    string readyMessage = "Звук и микрофон выбраны. Запрос на открытие ChatGPT отправлен; для разговора нажмите кнопку голоса на странице.")
{
    internal ChatPreparation(Func<CancellationToken, Task> releaseTv, Func<CancellationToken, Task> connectHeadset,
        Action selectAudio, Action openChat, Action<string> progress)
        : this(releaseTv, connectHeadset, selectAudio, _ => { openChat(); return Task.CompletedTask; }, progress) { }
    internal async Task RunAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        progress("Переключаю LG на динамики…");
        await releaseTv(token);
        token.ThrowIfCancellationRequested();
        progress("Подключаю наушники к ПК, проверяю звук и микрофон…");
        await connectHeadset(token);
        token.ThrowIfCancellationRequested();
        progress("Выбираю наушники и микрофон в Windows…");
        selectAudio();
        token.ThrowIfCancellationRequested();
        progress("Запускаю ChatGPT…");
        await openChat(token);
        progress(readyMessage);
    }
}
