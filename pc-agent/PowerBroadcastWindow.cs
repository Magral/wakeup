using System.Windows.Forms;
using System.Runtime.InteropServices;

// A hidden top-level window, NOT HWND_MESSAGE: Windows broadcasts power/session
// messages to top-level windows even while the desktop is locked.
internal sealed class PowerBroadcastWindow(TvPowerCoordinator coordinator, Action<string> log) : NativeWindow, IDisposable
{
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    internal void Start() => CreateHandle(new CreateParams
    {
        Caption = "Wake Mini power events", Style = unchecked((int)0x80000000), ExStyle = 0x80
    });
    protected override void WndProc(ref Message message)
    {
        switch (PowerEventPolicy.Decode(message.Msg, message.WParam.ToInt64(), message.LParam.ToInt64(),
            message.Msg == 0x16 && GetSystemMetrics(0x2000 /* SM_SHUTTINGDOWN */) != 0))
        {
            case TvPowerEvent.Suspend:
            case TvPowerEvent.Shutdown:
                try
                {
                    // PBT_APMSUSPEND grants approximately two seconds. An already
                    // authenticated connection avoids a fresh TLS/register handshake.
                    var pending = coordinator.PrepareDownAsync(TimeSpan.FromMilliseconds(1500));
                    if (!pending.Wait(TimeSpan.FromMilliseconds(1600))) log("Питание Windows: ожидание HDMI 2 завершено по лимиту.");
                }
                catch (Exception ex) { log("Событие питания Windows: " + ex.Message); }
                message.Result = IntPtr.Zero;
                return;
            case TvPowerEvent.Resume:
                _ = coordinator.ResumeAsync();
                message.Result = new IntPtr(1);
                return;
        }
        base.WndProc(ref message);
    }
    public void Dispose() => DestroyHandle();
}
