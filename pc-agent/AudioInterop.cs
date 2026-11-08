// Adapted from chrispoulter/bluetooth-audio-switch (MIT); see THIRD-PARTY-NOTICES.txt.
using System.Runtime.InteropServices;

namespace WakeMini.AudioInterop;

// Minimal Core Audio / Kernel Streaming COM definitions. Method order in each
// interface must match the native vtable exactly.

internal enum EDataFlow
{
    Render = 0,
    Capture = 1,
    All = 2,
}

[Flags]
internal enum DeviceState : uint
{
    Active = 0x1,
    Disabled = 0x2,
    NotPresent = 0x4,
    Unplugged = 0x8,
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey(Guid formatId, uint propertyId)
{
    public Guid FormatId = formatId;
    public uint PropertyId = propertyId;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropVariant
{
    public ushort VarType;
    public ushort Reserved1;
    public ushort Reserved2;
    public ushort Reserved3;
    public IntPtr Value;
    public IntPtr Value2;

    private const ushort VT_LPWSTR = 31;
    private const ushort VT_CLSID = 72;

    public readonly string? AsString() =>
        VarType == VT_LPWSTR ? Marshal.PtrToStringUni(Value) : null;

    public readonly Guid? AsGuid() =>
        VarType == VT_CLSID && Value != IntPtr.Zero ? Marshal.PtrToStructure<Guid>(Value) : null;
}

[StructLayout(LayoutKind.Sequential)]
internal struct KsProperty
{
    public Guid Set;
    public uint Id;
    public uint Flags;
}

internal static class PropertyKeys
{
    private static readonly Guid DeviceProperties = new("a45c254e-df1c-4efd-8020-67d146a850e0");

    public static readonly PropertyKey DeviceFriendlyName = new(DeviceProperties, 14);
    public static readonly PropertyKey DeviceInterfaceFriendlyName = new(
        new Guid("026e516e-b814-414b-83cd-856d6fef4822"),
        2
    );
    public static readonly PropertyKey DeviceContainerId = new(
        new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"),
        2
    );
}

internal static class KsBluetoothAudio
{
    /// <summary>KSPROPSETID_BtAudio.</summary>
    public static readonly Guid PropertySet = new("7fa06c40-b8f6-4c7e-8556-e8c33a12e54d");

    public const uint OneShotReconnect = 0;
    public const uint OneShotDisconnect = 1;

    public const uint KsPropertyTypeGet = 0x1;
}

internal static class ComIds
{
    public static readonly Guid MMDeviceEnumeratorClsid = new(
        "bcde0395-e52f-467c-8e3d-c4579291692e"
    );
    public static readonly Guid DeviceTopologyIid = new("2a07407e-6497-4a18-9787-32f79bd0d98f");
    public static readonly Guid KsControlIid = new("28f54685-06fd-11d2-b27a-00a0c9223196");

    public const uint ClsCtxAll = 0x17;
    public const uint StgmRead = 0;
}

[
    ComImport,
    Guid("a95664d2-9614-4f35-a746-de8db63617e6"),
    InterfaceType(ComInterfaceType.InterfaceIsIUnknown)
]
internal interface IMMDeviceEnumerator
{
    void EnumAudioEndpoints(
        EDataFlow dataFlow,
        DeviceState stateMask,
        out IMMDeviceCollection devices
    );
    void GetDefaultAudioEndpoint(EDataFlow dataFlow, int role, out IMMDevice endpoint);
    void GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    void RegisterEndpointNotificationCallback(IntPtr client);
    void UnregisterEndpointNotificationCallback(IntPtr client);
}

[
    ComImport,
    Guid("0bd7a1be-7a1a-44db-8397-cc5392387b5e"),
    InterfaceType(ComInterfaceType.InterfaceIsIUnknown)
]
internal interface IMMDeviceCollection
{
    void GetCount(out uint count);
    void Item(uint index, out IMMDevice device);
}

[
    ComImport,
    Guid("d666063f-1587-4e43-81f1-b948e807363f"),
    InterfaceType(ComInterfaceType.InterfaceIsIUnknown)
]
internal interface IMMDevice
{
    void Activate(
        ref Guid iid,
        uint clsCtx,
        IntPtr activationParams,
        [MarshalAs(UnmanagedType.IUnknown)] out object instance
    );
    void OpenPropertyStore(uint stgmAccess, out IPropertyStore properties);
    void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    void GetState(out DeviceState state);
}

[
    ComImport,
    Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"),
    InterfaceType(ComInterfaceType.InterfaceIsIUnknown)
]
internal interface IPropertyStore
{
    void GetCount(out uint count);
    void GetAt(uint index, out PropertyKey key);
    void GetValue(ref PropertyKey key, out PropVariant value);
    void SetValue(ref PropertyKey key, ref PropVariant value);
    void Commit();
}

[
    ComImport,
    Guid("2a07407e-6497-4a18-9787-32f79bd0d98f"),
    InterfaceType(ComInterfaceType.InterfaceIsIUnknown)
]
internal interface IDeviceTopology
{
    void GetConnectorCount(out uint count);
    void GetConnector(uint index, out IConnector connector);
    void GetSubunitCount(out uint count);
    void GetSubunit(uint index, out IntPtr subunit);
    void GetPartById(uint id, out IntPtr part);
    void GetDeviceId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    void GetSignalPath(
        IntPtr from,
        IntPtr to,
        [MarshalAs(UnmanagedType.Bool)] bool rejectMixedPaths,
        out IntPtr parts
    );
}

[
    ComImport,
    Guid("9c2c4058-23f5-41de-877a-df3af236a09e"),
    InterfaceType(ComInterfaceType.InterfaceIsIUnknown)
]
internal interface IConnector
{
    void GetConnectorType(out int type);
    void GetDataFlow(out int flow);
    void ConnectTo(IConnector connectTo);
    void Disconnect();
    void IsConnected([MarshalAs(UnmanagedType.Bool)] out bool connected);
    void GetConnectedTo(out IConnector connectedTo);
    void GetConnectorIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
    void GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
}

[
    ComImport,
    Guid("28f54685-06fd-11d2-b27a-00a0c9223196"),
    InterfaceType(ComInterfaceType.InterfaceIsIUnknown)
]
internal interface IKsControl
{
    [PreserveSig]
    int KsProperty(
        ref KsProperty property,
        uint propertyLength,
        IntPtr propertyData,
        uint dataLength,
        out uint bytesReturned
    );

    [PreserveSig]
    int KsMethod(
        IntPtr method,
        uint methodLength,
        IntPtr methodData,
        uint dataLength,
        out uint bytesReturned
    );

    [PreserveSig]
    int KsEvent(
        IntPtr ksEvent,
        uint eventLength,
        IntPtr eventData,
        uint dataLength,
        out uint bytesReturned
    );
}

internal static class NativeMethods
{
    [DllImport("ole32.dll")]
    public static extern int PropVariantClear(ref PropVariant value);
}
