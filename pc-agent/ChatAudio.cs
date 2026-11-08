using System.Runtime.InteropServices;
using WakeMini.AudioInterop;

internal sealed record ChatAudioEndpoint(string Id, string Name, bool Microphone, bool Active)
{
    public override string ToString() => Name + (Active ? " — подключено" : " — отключено");
}

internal static class ChatAudio
{
    private static IMMDeviceEnumerator Enumerator() => (IMMDeviceEnumerator)Activator.CreateInstance(
        Type.GetTypeFromCLSID(ComIds.MMDeviceEnumeratorClsid, true)!)!;
    private static void Release(object? value) { if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }

    internal static List<ChatAudioEndpoint> List()
    {
        var result = new List<ChatAudioEndpoint>();
        var enumerator = Enumerator();
        try
        {
            foreach (var flow in new[] { EDataFlow.Render, EDataFlow.Capture })
            {
                enumerator.EnumAudioEndpoints(flow, DeviceState.Active | DeviceState.Unplugged, out var devices);
                try
                {
                    devices.GetCount(out var count);
                    for (uint i = 0; i < count; i++)
                    {
                        devices.Item(i, out var device);
                        try
                        {
                            device.GetId(out var id); device.GetState(out var state);
                            device.OpenPropertyStore(ComIds.StgmRead, out var properties);
                            string name;
                            try
                            {
                                var key = PropertyKeys.DeviceFriendlyName;
                                properties.GetValue(ref key, out var value);
                                try { name = value.AsString() ?? "Аудиоустройство"; }
                                finally { NativeMethods.PropVariantClear(ref value); }
                            }
                            finally { Release(properties); }
                            result.Add(new(id, name, flow == EDataFlow.Capture, state == DeviceState.Active));
                        }
                        finally { Release(device); }
                    }
                }
                finally { Release(devices); }
            }
        }
        finally { Release(enumerator); }
        return result;
    }

    internal static void Validate(string renderId, string captureId)
    {
        var devices = List();
        if (!devices.Any(x => x.Id == renderId && !x.Microphone)
            || !devices.Any(x => x.Id == captureId && x.Microphone))
            throw new InvalidOperationException("Выберите выход наушников и микрофон в настройках разговора. Если их нет, сначала подключите FreeBuds к Windows вручную.");
    }

    internal static bool Ready(string renderId, string captureId)
    {
        var devices = List();
        return devices.Any(x => x.Id == renderId && !x.Microphone && x.Active)
            && devices.Any(x => x.Id == captureId && x.Microphone && x.Active);
    }

    // Ask the Bluetooth audio driver to connect. Do not disable services,
    // remove pairing, toggle the adapter or touch other Bluetooth devices.
    private static void Reconnect(string endpointId)
    {
        var enumerator = Enumerator();
        object? topologyObject = null, controlObject = null;
        IMMDevice? endpoint = null, filter = null;
        IConnector? connector = null;
        try
        {
            enumerator.GetDevice(endpointId, out endpoint);
            var iid = ComIds.DeviceTopologyIid;
            endpoint.Activate(ref iid, ComIds.ClsCtxAll, IntPtr.Zero, out topologyObject);
            ((IDeviceTopology)topologyObject).GetConnector(0, out connector);
            connector.GetDeviceIdConnectedTo(out var filterId);
            enumerator.GetDevice(filterId, out filter);
            iid = ComIds.KsControlIid;
            filter.Activate(ref iid, ComIds.ClsCtxAll, IntPtr.Zero, out controlObject);
            var property = new KsProperty { Set = KsBluetoothAudio.PropertySet,
                Id = KsBluetoothAudio.OneShotReconnect, Flags = KsBluetoothAudio.KsPropertyTypeGet };
            Marshal.ThrowExceptionForHR(((IKsControl)controlObject).KsProperty(ref property,
                (uint)Marshal.SizeOf<KsProperty>(), IntPtr.Zero, 0, out _));
        }
        finally
        {
            Release(controlObject); Release(filter); Release(connector);
            Release(topologyObject); Release(endpoint); Release(enumerator);
        }
    }

    internal static async Task ConnectAsync(string renderId, string captureId, CancellationToken token)
    {
        if (Ready(renderId, captureId)) return;
        var accepted = false;
        foreach (var id in new[] { renderId, captureId }.Distinct())
        {
            token.ThrowIfCancellationRequested();
            try { Reconnect(id); accepted = true; }
            catch (COMException) { /* Some offloaded audio filters do not expose IKsControl. */ }
        }
        if (!accepted) throw new InvalidOperationException("Драйвер Bluetooth не принял запрос подключения. Проверьте ручное подключение наушников в Windows.");
        for (var attempt = 0; attempt < 40; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (Ready(renderId, captureId)) return;
            await Task.Delay(500, token);
        }
        throw new IOException("Windows не подтвердила готовность звука и микрофона наушников за 20 секунд. Проверьте, что FreeBuds включены и не подключились обратно к ТВ.");
    }

    internal static void SelectDefaults(string renderId, string captureId)
    {
        if (!Ready(renderId, captureId)) throw new InvalidOperationException("Наушники или микрофон отключены.");
        var enumerator = Enumerator();
        object? policyObject = null;
        var previous = new List<(EDataFlow Flow, int Role, string Id)>();
        try
        {
            policyObject = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9"), true)!);
            var policy = (IChatPolicyConfig)policyObject!;
            foreach (var flow in new[] { EDataFlow.Render, EDataFlow.Capture })
            for (var role = 0; role < 3; role++)
            {
                enumerator.GetDefaultAudioEndpoint(flow, role, out var device);
                try { device.GetId(out var id); previous.Add((flow, role, id)); }
                finally { Release(device); }
            }
            try
            {
                foreach (var entry in previous)
                    Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(entry.Flow == EDataFlow.Render ? renderId : captureId, entry.Role));
                foreach (var entry in previous)
                {
                    enumerator.GetDefaultAudioEndpoint(entry.Flow, entry.Role, out var device);
                    try
                    {
                        device.GetId(out var id);
                        if (id != (entry.Flow == EDataFlow.Render ? renderId : captureId))
                            throw new InvalidOperationException("Windows не подтвердила выбор звуковых устройств.");
                    }
                    finally { Release(device); }
                }
            }
            catch
            {
                foreach (var entry in previous) { try { policy.SetDefaultEndpoint(entry.Id, entry.Role); } catch { } }
                throw;
            }
        }
        finally { Release(policyObject); Release(enumerator); }
    }
}

// Windows PolicyConfig is an undocumented compatibility interface. Fail with a
// visible error if unavailable; do not fall back to registry edits. The first
// ten members reserve the native slots; only SetDefaultEndpoint is invoked.
[ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IChatPolicyConfig
{
    [PreserveSig] int GetMixFormat();
    [PreserveSig] int GetDeviceFormat();
    [PreserveSig] int ResetDeviceFormat();
    [PreserveSig] int SetDeviceFormat();
    [PreserveSig] int GetProcessingPeriod();
    [PreserveSig] int SetProcessingPeriod();
    [PreserveSig] int GetShareMode();
    [PreserveSig] int SetShareMode();
    [PreserveSig] int GetPropertyValue();
    [PreserveSig] int SetPropertyValue();
    [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
    [PreserveSig] int SetEndpointVisibility();
}
