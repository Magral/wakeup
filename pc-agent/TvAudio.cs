using System.Runtime.InteropServices;
using WakeMini.AudioInterop;

internal static partial class Program
{
    private static string ReturnSoundToTv(string directory)
    {
        lock (TvLock)
        {
            if (ChatOperation != null || ChatStopping || TvOperation != null || Volatile.Read(ref DesktopBusy) != 0) return "SOUND_TV_BUSY";
            var settings = ReadChatSettings(directory);
            var tv = ReadTvSettings(directory);
            if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.TvOutputId)) return "SOUND_TV_NOT_CONFIGURED";
            try { tv.Validate(); } catch { return "SOUND_TV_NOT_CONFIGURED"; }
            var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            ChatOperation = cancellation; ChatState = "BUSY";
            _ = Task.Run(async () =>
            {
                try
                {
                    if (VoiceBridge.Connected) await VoiceBridge.SendAsync("stop", cancellation.Token);
                    ChatAudio.SelectOutput(settings.TvOutputId);
                    await new LgWebOsClient(tv, _ => { }).ReleaseBluetoothAudioAsync(cancellation.Token);
                    lock (TvLock) { ChatState = "READY"; ChatMessage = "Windows выводит звук на HDMI телевизора; LG подтвердил динамики."; }
                }
                catch (Exception ex) { lock (TvLock) { ChatState = "FAILED"; ChatMessage = "Возврат звука: " + ex.Message; } }
                finally
                {
                    Log(directory, ChatMessage);
                    lock (TvLock) { ChatOperation = null; cancellation.Dispose(); }
                }
            });
            return "SOUND_TV_ACCEPTED";
        }
    }
}

internal static partial class ChatAudio
{
    internal static void SelectOutput(string id)
    {
        if (!List().Any(x => x.Id == id && !x.Microphone && x.Active))
            throw new InvalidOperationException("Выход HDMI телевизора недоступен. Включите ТВ и проверьте настройки звука Windows.");
        var enumerator = Enumerator();
        object? policyObject = null;
        var previous = new List<(int Role, string Id)>();
        try
        {
            policyObject = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9"), true)!);
            var policy = (IChatPolicyConfig)policyObject!;
            for (var role = 0; role < 3; role++)
            {
                enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, role, out var device);
                try { device.GetId(out var oldId); previous.Add((role, oldId)); }
                finally { Release(device); }
            }
            try
            {
                foreach (var item in previous) Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(id, item.Role));
                foreach (var item in previous)
                {
                    enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, item.Role, out var device);
                    try { device.GetId(out var actual); if (actual != id) throw new IOException("Windows не подтвердила выход HDMI."); }
                    finally { Release(device); }
                }
            }
            catch
            {
                foreach (var item in previous) { try { policy.SetDefaultEndpoint(item.Id, item.Role); } catch { } }
                throw;
            }
        }
        finally { Release(policyObject); Release(enumerator); }
    }
}
