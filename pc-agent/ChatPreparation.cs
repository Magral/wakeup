// Testable sequencing; Windows audio and TV transports are supplied by the host.
internal sealed class ChatPreparation(
    Func<CancellationToken, Task> releaseTv,
    Func<CancellationToken, Task> connectHeadset,
    Action selectAudio,
    Action openChat,
    Action<string> progress)
{
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
        progress("Открываю ChatGPT в Chrome…");
        openChat();
        progress("Звук и микрофон выбраны. Запрос на открытие ChatGPT отправлен; для разговора нажмите кнопку голоса на странице.");
    }
}
