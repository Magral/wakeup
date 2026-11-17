using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

internal static class WindowsLogonIdentity
{
    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct TokenStatistics
    {
        public Luid TokenId, AuthenticationId;
        public long ExpirationTime;
        public int TokenType, ImpersonationLevel;
        public uint DynamicCharged, DynamicAvailable, GroupCount, PrivilegeCount;
        public Luid ModifiedId;
    }
    [StructLayout(LayoutKind.Sequential)] private struct UnicodeString
    { public ushort Length, MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)] private struct LogonSessionHeader
    {
        public uint Size; public Luid LogonId;
        public UnicodeString UserName, LogonDomain, AuthenticationPackage;
        public uint LogonType, Session; public IntPtr Sid; public long LogonTime;
    }
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass,
        out TokenStatistics statistics, int length, out int returnedLength);
    [DllImport("secur32.dll")] private static extern uint LsaGetLogonSessionData(ref Luid id, out IntPtr data);
    [DllImport("secur32.dll")] private static extern uint LsaFreeReturnBuffer(IntPtr data);
    internal static string Read()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!GetTokenInformation(identity.AccessToken, 10 /* TokenStatistics */, out var stats,
            Marshal.SizeOf<TokenStatistics>(), out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (LsaGetLogonSessionData(ref stats.AuthenticationId, out var data) != 0 || data == IntPtr.Zero)
            throw new InvalidOperationException("Не удалось определить текущую сессию входа Windows.");
        try
        {
            var header = Marshal.PtrToStructure<LogonSessionHeader>(data);
            if (header.Size < Marshal.SizeOf<LogonSessionHeader>() || header.LogonTime <= 0)
                throw new InvalidOperationException("Windows не вернула время текущего входа.");
            // The logon time disambiguates LUID reuse across boots, including Fast Startup.
            var value = $"{stats.AuthenticationId.High:X8}{stats.AuthenticationId.Low:X8}|{header.LogonTime}|{identity.User?.Value}";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
        }
        finally { LsaFreeReturnBuffer(data); }
    }
}
