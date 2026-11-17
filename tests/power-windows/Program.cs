using System.Collections.Concurrent;
using System.Runtime.InteropServices;
internal static class Program
{
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [STAThread] private static void Main()
    {
        var session = WindowsLogonIdentity.Read();
        if (session.Length != 64 || session != WindowsLogonIdentity.Read()) throw new Exception("Windows logon identity is not stable");
        var selected = new ConcurrentQueue<int>();
        using var coordinator = new TvPowerCoordinator(() => true, (input, _) => { selected.Enqueue(input); return Task.CompletedTask; }, () => {}, _ => {});
        using var window = new PowerBroadcastWindow(coordinator, Console.WriteLine); window.Start();
        void Message(uint message, long value, long flags = 0) => SendMessage(window.Handle, message, new IntPtr(value), new IntPtr(flags));
        void Expect(params int[] inputs)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (selected.Count < inputs.Length && watch.Elapsed < TimeSpan.FromSeconds(3)) Thread.Sleep(10);
            if (!selected.SequenceEqual(inputs)) throw new Exception("Native window routed unexpected inputs: " + string.Join(",", selected));
        }
        Message(0x218,4); Expect(2); // Fake notification to OUR hidden window only; no OS power API.
        Message(0x218,18); Expect(2,1);
        Message(0x218,7); Thread.Sleep(50); Expect(2,1);
        Message(0x16,1,0x80000000L); Expect(2,1); // logoff excluded
        Message(0x16,1,1); Expect(2,1); // updating helper excluded
        Message(0x16,0); Expect(2,1); // canceled shutdown excluded
        Message(0x16,1); Expect(2,1,2); // confirmed shutdown
        Console.WriteLine("Real Windows hidden-window message routing passed; no OS sleep/shutdown was requested.");
    }
}
