using System.Diagnostics;
using System.Runtime.InteropServices;
using WakeMini.AudioInterop;

// Core Audio session interfaces follow audiopolicy.h / audioclient.h vtable order.
// https://learn.microsoft.com/windows/win32/api/audiopolicy/nn-audiopolicy-iaudiosessionmanager2
internal static class GameAudioSessions
{
    internal static void Visit(Action<GameAudioSession, string, Action<float>> visit)
    {
        var enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(ComIds.MMDeviceEnumeratorClsid, true)!)!;
        try
        {
            enumerator.EnumAudioEndpoints(EDataFlow.Render, DeviceState.Active, out var devices);
            try
            {
                devices.GetCount(out var count);
                for (uint i = 0; i < count; i++)
                {
                    devices.Item(i, out var device);
                    object? managerObject = null; IGameAudioSessionEnumerator? sessions = null;
                    try
                    {
                        device.GetId(out var endpoint);
                        var iid = typeof(IGameAudioSessionManager).GUID;
                        device.Activate(ref iid, ComIds.ClsCtxAll, IntPtr.Zero, out managerObject);
                        ((IGameAudioSessionManager)managerObject).GetSessionEnumerator(out sessions);
                        sessions.GetCount(out var length);
                        for (var j = 0; j < length; j++)
                        {
                            object? control = null;
                            try
                            {
                                sessions.GetSession(j, out control);
                                var session = (IGameAudioSessionControl)control;
                                if (session.GetProcessId(out var pid) != 0 || pid == 0) continue;
                                using var process = Process.GetProcessById((int)pid);
                                var path = process.MainModule?.FileName;
                                if (string.IsNullOrEmpty(path)) continue;
                                session.GetSessionIdentifier(out var recoveryId);
                                session.GetSessionInstanceIdentifier(out var id);
                                session.GetState(out var sessionState);
                                var volume = (IGameSimpleAudioVolume)control;
                                volume.GetMasterVolume(out var level);
                                var processKey = path + "|" + pid + "|" + process.StartTime.ToUniversalTime().Ticks;
                                visit(new(endpoint + "|" + id, processKey, level, endpoint + "|" + recoveryId, endpoint, sessionState == 1), path, target =>
                                {
                                    var context = new Guid("067cbb63-3d69-4770-b16e-019e14a4ed15");
                                    volume.SetMasterVolume(target, ref context);
                                });
                            }
                            catch (Exception ex) when (ex is COMException or InvalidCastException or InvalidOperationException
                                or System.ComponentModel.Win32Exception or ArgumentException) { /* Exited/protected process. */ }
                            finally { Release(control); }
                        }
                    }
                    catch (COMException) { /* Endpoint disappeared during Bluetooth/HDMI migration. */ }
                    finally { Release(sessions); Release(managerObject); Release(device); }
                }
            }
            finally { Release(devices); }
        }
        finally { Release(enumerator); }
    }
    private static void Release(object? value) { if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
}

[ComImport, Guid("77aa99a0-1bd6-484f-8bc7-2c654c9a9b6f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IGameAudioSessionManager
{
    void GetAudioSessionControl(IntPtr sessionGuid, uint flags, out IntPtr control);
    void GetSimpleAudioVolume(IntPtr sessionGuid, uint flags, out IntPtr volume);
    void GetSessionEnumerator(out IGameAudioSessionEnumerator sessions);
}
[ComImport, Guid("e2f5bb11-0570-40ca-acdd-3aa01277dee8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IGameAudioSessionEnumerator
{
    void GetCount(out int count);
    void GetSession(int index, [MarshalAs(UnmanagedType.IUnknown)] out object control);
}
[ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IGameAudioSessionControl
{
    void GetState(out int state);
    void GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
    void SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid context);
    void GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
    void SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid context);
    void GetGroupingParam(out Guid grouping);
    void SetGroupingParam(ref Guid grouping, ref Guid context);
    void RegisterAudioSessionNotification(IntPtr notification);
    void UnregisterAudioSessionNotification(IntPtr notification);
    void GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
    void GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetProcessId(out uint pid);
}
[ComImport, Guid("87ce5498-68d6-44e5-9215-6da47ef883d8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IGameSimpleAudioVolume
{
    void SetMasterVolume(float level, ref Guid context);
    void GetMasterVolume(out float level);
    void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
    void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}
