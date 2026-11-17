internal enum TvPowerEvent { None, Suspend, Resume, Shutdown }
internal static class PowerEventPolicy
{
    internal static TvPowerEvent Decode(int message, long wParam, long lParam)
    {
        if (message == 0x218) return wParam switch
        {
            4 => TvPowerEvent.Suspend,
            6 or 7 or 18 => TvPowerEvent.Resume,
            _ => TvPowerEvent.None
        };
        // ENDSESSION_LOGOFF and ENDSESSION_CLOSEAPP must not turn an ordinary
        // logoff / Restart Manager update of the helper into a PC power event.
        if (message == 0x16 && wParam != 0 && (lParam & 0x80000001L) == 0) return TvPowerEvent.Shutdown;
        return TvPowerEvent.None;
    }
}
