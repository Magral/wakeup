using System.Diagnostics;
using System.Text.Json;
using System.Windows.Forms;

internal static partial class Program
{
    private static CancellationTokenSource? ChatOperation;
    private static string ChatState = "IDLE";
    private static string ChatMessage = "Подготовка разговора ещё не запускалась.";
    private sealed class ConversationSettings
    {
        public bool Enabled { get; set; }
        public string ChromePath { get; set; } = "";
        public string OutputId { get; set; } = "";
        public string MicrophoneId { get; set; } = "";
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
    private static string StartConversation(string directory, bool tvOnly = false)
    {
        lock (TvLock)
        {
            if (ChatOperation != null || TvOperation != null) return "CHATGPT_BUSY";
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
            var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
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
                        var workflow = new ChatPreparation(ReleaseTv,
                            token => ChatAudio.ConnectAsync(settings.OutputId, settings.MicrophoneId, token),
                            () => ChatAudio.SelectDefaults(settings.OutputId, settings.MicrophoneId),
                            () =>
                            {
                                Process.Start(new ProcessStartInfo(settings.ChromePath) { UseShellExecute = true,
                                    Arguments = "https://chatgpt.com/", WorkingDirectory = Path.GetDirectoryName(settings.ChromePath) });
                                BringProgramForward("chrome");
                            }, Progress);
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
            "Один раз подключите наушники к Windows и выберите их ниже. Профили Работа / Игры / Кино сохраняются." });
        var enabled = Row(grid, new CheckBox { AutoSize = true, Text = "Разрешить подготовку разговора по команде Алисы", Checked = settings.Enabled });
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
        var input = Select("Микрофон — выберите микрофон FreeBuds SE 2", true, settings.MicrophoneId);
        Row(grid, new Label { AutoSize = true, MaximumSize = new(700, 0), Text =
            "Если устройства нет в списке, подключите наушники к Windows и откройте это окно заново. " +
            "Браузер должен использовать системный микрофон; разрешение на микрофон и запуск голоса выполняются на странице ChatGPT. " +
            "Проверка LG переключает звук телевизора на динамики. Возврат наушников к ТВ пока выполняется вручную." });
        var save = Row(grid, new Button { Text = "Сохранить", AutoSize = true });
        var testTv = Row(grid, new Button { Text = "Проверить LG: переключить на динамики", AutoSize = true });
        var testAll = Row(grid, new Button { Text = "Подготовить разговор сейчас", AutoSize = true });
        var cancel = Row(grid, new Button { Text = "Остановить подготовку", AutoSize = true });
        var status = Row(grid, new Label { AutoSize = true, MaximumSize = new(700, 0) });
        bool Persist()
        {
            try
            {
                ValidateChrome(chrome.Text.Trim());
                if (output.SelectedItem is not ChatAudioEndpoint o || input.SelectedItem is not ChatAudioEndpoint i)
                    throw new InvalidOperationException("Выберите наушники и микрофон.");
                settings = new() { Enabled = enabled.Checked, ChromePath = chrome.Text.Trim(), OutputId = o.Id, MicrophoneId = i.Id };
                SaveChatSettings(directory, settings); return true;
            }
            catch (Exception ex) { status.Text = ex.Message; return false; }
        }
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
        using var timer = new System.Windows.Forms.Timer { Interval = 500 };
        string previous; lock (TvLock) previous = ChatMessage;
        status.Text = previous;
        timer.Tick += (_, _) => { string message; lock (TvLock) message = ChatMessage; if (message != previous) { previous = message; status.Text = message; } };
        timer.Start(); form.ShowDialog();
    }
}
