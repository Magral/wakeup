using System.Diagnostics;
using System.Text.Json;
using System.Windows.Forms;

internal static partial class Program
{
    private static CancellationTokenSource? ChatOperation;
    private static readonly VoiceNativeBridge VoiceBridge = new();
    private static bool ChatStopping;
    private static string ChatState = "IDLE";
    private static string ChatMessage = "Подготовка разговора ещё не запускалась.";
    private sealed class ConversationSettings
    {
        public bool Enabled { get; set; }
        public string ChromePath { get; set; } = "";
        public string OutputId { get; set; } = "";
        public string TvOutputId { get; set; } = "";
        public string MicrophoneId { get; set; } = "";
        public bool BackgroundVoice { get; set; }
    }
    private static string ChatPath(string directory) => Path.Combine(directory, "wake-chat.json");
    private static ConversationSettings ReadChatSettings(string directory) => File.Exists(ChatPath(directory))
        ? JsonSerializer.Deserialize<ConversationSettings>(File.ReadAllText(ChatPath(directory))) ?? new() : new();
    private static void SaveChatSettings(string directory, ConversationSettings settings)
    {
        var path = ChatPath(directory);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
    private static string DetectChrome() => new[] {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe")
    }.FirstOrDefault(File.Exists) ?? "";
    private static void ValidateChrome(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path)
            || !string.Equals(Path.GetFileName(path), "chrome.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Выберите установленный Google Chrome — файл chrome.exe.");
    }
    private static async Task EnsureVoiceExtensionAsync(string chromePath, CancellationToken token)
    {
        if (!VoiceBridge.Connected)
        {
            Process.Start(new ProcessStartInfo(chromePath)
            {
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Minimized,
                WorkingDirectory = Path.GetDirectoryName(chromePath)
            });
            for (var attempt = 0; attempt < 60; attempt++)
            {
                token.ThrowIfCancellationRequested();
                if (VoiceBridge.Connected)
                {
                    MinimizeProgramWindows("chrome");
                    return;
                }
                await Task.Delay(250, token);
            }
            throw new IOException("Chrome запущен свёрнутым, но расширение Wake Mini Voice не подключилось. Проверьте, что расширение установлено и включено.");
        }
    }

    private static string StartConversation(string directory, bool tvOnly = false)
    {
        lock (TvLock)
        {
            if (ChatStopping || ChatOperation != null || TvOperation != null || Volatile.Read(ref DesktopBusy) != 0) return "CHATGPT_BUSY";
            ConversationSettings settings;
            LgTvSettings tv;
            try
            {
                tv = ReadTvSettings(directory); tv.Validate();
                if (string.IsNullOrWhiteSpace(tv.ClientKey) || tv.Secure && string.IsNullOrWhiteSpace(tv.CertificateHash))
                    return "CHATGPT_NOT_CONFIGURED";
                settings = tvOnly ? new ConversationSettings() : ReadChatSettings(directory);
                if (!tvOnly)
                {
                    if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.OutputId) || string.IsNullOrWhiteSpace(settings.MicrophoneId))
                        return "CHATGPT_NOT_CONFIGURED";
                    ValidateChrome(settings.ChromePath);
                }
            }
            catch { return "CHATGPT_NOT_CONFIGURED"; }
            var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(settings.BackgroundVoice ? 90 : 45));
            ChatOperation = cancellation; ChatState = "BUSY";
            ChatMessage = "Запрос принят. Готовлю переключение звука…";
            _ = Task.Run(async () =>
            {
                void Progress(string message) { lock (TvLock) ChatMessage = message; }
                string state, message;
                try
                {
                    async Task ReleaseTv(CancellationToken token)
                    {
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                        deadline.CancelAfter(TimeSpan.FromSeconds(12));
                        await new LgWebOsClient(tv, _ => { }).ReleaseBluetoothAudioAsync(deadline.Token);
                        await Task.Delay(1000, token); // Let the TV release its Bluetooth transport.
                    }
                    if (tvOnly)
                    {
                        Progress("Проверяю переключение LG на динамики…");
                        await ReleaseTv(cancellation.Token);
                        message = "LG подтвердил вывод на динамики. Теперь можно проверить полное подключение FreeBuds к ПК.";
                    }
                    else
                    {
                        ChatAudio.Validate(settings.OutputId, settings.MicrophoneId);
                        var headsetReady = ChatAudio.Ready(settings.OutputId, settings.MicrophoneId);
                        var workflow = new ChatPreparation(
                            token => headsetReady ? Task.CompletedTask : ReleaseTv(token),
                            token => ChatAudio.ConnectAsync(settings.OutputId, settings.MicrophoneId, token),
                            () => ChatAudio.SelectDefaults(settings.OutputId, settings.MicrophoneId),
                            async token =>
                            {
                                if (settings.BackgroundVoice)
                                {
                                    await EnsureVoiceExtensionAsync(settings.ChromePath, token);
                                    await VoiceBridge.SendAsync("start", token);
                                }
                                else
                                {
                                    Process.Start(new ProcessStartInfo(settings.ChromePath) { UseShellExecute = true,
                                        Arguments = "https://chatgpt.com/", WorkingDirectory = Path.GetDirectoryName(settings.ChromePath) });
                                    BringProgramForward("chrome");
                                }
                            }, Progress, settings.BackgroundVoice
                                ? "Интерфейс ChatGPT подтвердил голосовой режим. Окно игры не переключалось; проверьте слышимость в наушниках."
                                : "Звук и микрофон выбраны. Запрос на открытие ChatGPT отправлен; для разговора нажмите кнопку голоса на странице.");
                        await workflow.RunAsync(cancellation.Token);
                        lock (TvLock) message = ChatMessage;
                    }
                    state = "READY";
                }
                catch (OperationCanceledException) { state = "FAILED"; message = "Подготовка отменена или истекло время ожидания. Уже выполненное переключение звука LG не отменяется."; }
                catch (Exception ex) { state = "FAILED"; message = "Подготовка не завершена: " + ex.Message; }
                lock (TvLock)
                {
                    ChatState = state; ChatMessage = message;
                    ChatOperation = null; cancellation.Dispose();
                }
                Log(directory, "ChatGPT: " + message);
            });
            return "CHATGPT_ACCEPTED";
        }
    }
    private static string ConversationStatus() { lock (TvLock) return "CHAT_STATUS_" + ChatState; }
    private static string ConversationDiagnostic() { lock (TvLock) return ChatMessage; }
    private static void CancelConversation() { lock (TvLock) ChatOperation?.Cancel(); }
    private static string StopBackgroundConversation(string directory)
    {
        lock (TvLock)
        {
            if (!VoiceBridge.Connected) return "CHAT_END_NOT_CONFIGURED";
            if (ChatStopping) return "CHAT_END_ACCEPTED";
            ChatStopping = true;
            ChatOperation?.Cancel();
        }
        _ = Task.Run(async () =>
        {
            try
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(85));
                // Let cancellation finish before sending stop: the content script serializes clicks.
                while (true)
                {
                    lock (TvLock) { if (ChatOperation == null) break; }
                    await Task.Delay(100, stop.Token);
                }
                await VoiceBridge.SendAsync("stop", stop.Token);
                lock (TvLock) { ChatState = "IDLE"; ChatMessage = "Интерфейс ChatGPT подтвердил завершение голосового режима."; }
            }
            catch (Exception ex) { lock (TvLock) { ChatState = "FAILED"; ChatMessage = "Завершение не подтверждено: " + ex.Message; } }
            finally { lock (TvLock) ChatStopping = false; }
            Log(directory, "ChatGPT: " + ConversationDiagnostic());
        });
        return "CHAT_END_ACCEPTED";
    }

    private static void ConfigureConversation(string directory)
    {
        ConversationSettings settings;
        List<ChatAudioEndpoint> devices;
        try { settings = ReadChatSettings(directory); devices = ChatAudio.List(); }
        catch (Exception ex) { MessageBox.Show("Не удалось получить аудиоустройства: " + ex.Message, "Wake Mini"); return; }
        var window = SettingsWindow("разговор с ChatGPT / FreeBuds"); using var form = window.Form;
        var grid = window.Grid;
        Row(grid, new Label { AutoSize = true, MaximumSize = new(700, 0), Text =
            "LG C5 → динамики → FreeBuds SE 2 на ПК → звук и микрофон → ChatGPT в Chrome. " +
            "Один раз подключите наушники к Windows и выберите их ниже. В фоновом режиме Chrome запустится по команде, подключится к расширению и откроет голос ChatGPT." });
        var enabled = Row(grid, new CheckBox { AutoSize = true, Text = "Разрешить подготовку разговора по команде Алисы", Checked = settings.Enabled });
        var background = Row(grid, new CheckBox { AutoSize = true, Text = "Запускать голос ChatGPT в фоне через расширение Chrome", Checked = settings.BackgroundVoice });
        var register = Row(grid, new Button { AutoSize = true, Text = "Подключить помощник к расширению Chrome" });
        var chrome = Field(grid, "Google Chrome (chrome.exe)", string.IsNullOrEmpty(settings.ChromePath) ? DetectChrome() : settings.ChromePath);
        var browse = Row(grid, new Button { AutoSize = true, Text = "Выбрать Chrome…" });
        browse.Click += (_, _) => { using var picker = new OpenFileDialog { Filter = "Google Chrome (chrome.exe)|chrome.exe" }; if (picker.ShowDialog(form) == DialogResult.OK) chrome.Text = picker.FileName; };
        ComboBox Select(string title, bool microphone, string id)
        {
            Row(grid, new Label { Text = title, AutoSize = true });
            var combo = Row(grid, new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList });
            foreach (var device in devices.Where(x => x.Microphone == microphone)) combo.Items.Add(device);
            var preferred = devices.FirstOrDefault(x => x.Id == id && x.Microphone == microphone)
                ?? devices.FirstOrDefault(x => x.Microphone == microphone && x.Name.Contains("FreeBuds SE 2", StringComparison.OrdinalIgnoreCase));
            if (preferred != null) combo.SelectedItem = preferred;
            return combo;
        }
        var output = Select("Звук — выберите FreeBuds SE 2", false, settings.OutputId);
        Row(grid, new Label { Text = "Звук телевизора — выберите LG / HDMI / NVIDIA High Definition Audio", AutoSize = true });
        var tvOutput = Row(grid, new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList });
        foreach (var device in devices.Where(x => !x.Microphone)) tvOutput.Items.Add(device);
        tvOutput.SelectedItem = devices.FirstOrDefault(x => x.Id == settings.TvOutputId && !x.Microphone);
        var returnTv = Row(grid, new Button { Text = "Вернуть звук на телевизор", AutoSize = true });
        var input = Select("Микрофон — выберите микрофон FreeBuds SE 2", true, settings.MicrophoneId);
        Row(grid, new Label { AutoSize = true, MaximumSize = new(700, 0), Text =
            "Если устройства нет в списке, подключите наушники к Windows и откройте это окно заново. " +
            "В Chrome заранее войдите в ChatGPT и один раз разрешите доступ к микрофону; при команде голос включится автоматически. " +
            "Проверка LG переключает звук телевизора на динамики. Возврат наушников к ТВ пока выполняется вручную." });
        var save = Row(grid, new Button { Text = "Сохранить", AutoSize = true });
        var testTv = Row(grid, new Button { Text = "Проверить LG: переключить на динамики", AutoSize = true });
        var testAll = Row(grid, new Button { Text = "Подготовить разговор сейчас", AutoSize = true });
        var cancel = Row(grid, new Button { Text = "Остановить подготовку", AutoSize = true });
        var endVoice = Row(grid, new Button { Text = "Завершить голосовой разговор", AutoSize = true });
        var status = Row(grid, new Label { AutoSize = true, MaximumSize = new(700, 0) });
        bool Persist()
        {
            try
            {
                ValidateChrome(chrome.Text.Trim());
                if (output.SelectedItem is not ChatAudioEndpoint o || input.SelectedItem is not ChatAudioEndpoint i)
                    throw new InvalidOperationException("Выберите наушники и микрофон.");
                settings = new() { Enabled = enabled.Checked, ChromePath = chrome.Text.Trim(), OutputId = o.Id, MicrophoneId = i.Id,
                    BackgroundVoice = background.Checked, TvOutputId = (tvOutput.SelectedItem as ChatAudioEndpoint)?.Id ?? "" };
                SaveChatSettings(directory, settings); return true;
            }
            catch (Exception ex) { status.Text = ex.Message; return false; }
        }
        returnTv.Click += (_, _) => { if (Persist()) status.Text = ReturnSoundToTv(directory); };
        save.Click += (_, _) => { if (Persist()) status.Text = "Настройки сохранены."; };
        void Start(bool tvOnly)
        {
            if (!tvOnly && !Persist()) return;
            status.Text = StartConversation(directory, tvOnly) switch {
                "CHATGPT_ACCEPTED" => "Запрос принят. Ниже появится результат каждого этапа.",
                "CHATGPT_BUSY" => "Управление ТВ или подготовка разговора уже выполняется.",
                _ => "Проверьте сопряжение LG и настройки разговора; для полной проверки включите разрешение команды." };
        }
        testTv.Click += (_, _) => Start(true);
        testAll.Click += (_, _) => Start(false);
        cancel.Click += (_, _) => CancelConversation();
        register.Click += (_, _) => { try { VoiceNativeBridge.RegisterHost(directory); status.Text = "Связь зарегистрирована. Установите папку chrome-voice-extension на странице chrome://extensions. Оставлять Chrome запущенным не нужно — помощник откроет его при команде."; } catch (Exception ex) { status.Text = ex.Message; } };
        endVoice.Click += (_, _) => { status.Text = StopBackgroundConversation(directory) == "CHAT_END_ACCEPTED" ? "Завершаю голосовой разговор…" : "Нет связи с расширением Chrome."; };
        using var timer = new System.Windows.Forms.Timer { Interval = 500 };
        string previous; lock (TvLock) previous = ChatMessage;
        status.Text = previous;
        timer.Tick += (_, _) => { string message; lock (TvLock) message = ChatMessage; if (message != previous) { previous = message; status.Text = message; } };
        timer.Start(); form.ShowDialog();
    }
}
