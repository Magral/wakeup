# TV input from Windows power events

Goal: Windows startup/resume selects HDMI1; confirmed shutdown/restart and suspend select HDMI2, regardless of which application requested the transition.

Architecture: hidden top-level native window receives WM_POWERBROADCAST and WM_ENDSESSION. A cancellable coordinator serializes input operations with existing manual TV commands. A pinned authenticated LG WebSocket is kept ready, so suspend can send HDMI2 within Windows' short notification budget. A saved-key one-shot fallback never wakes the TV for HDMI2. Startup/resume retains existing WoL/retry behavior.

Constraints: keep existing pairing/TLS pins; never veto Windows power transitions; no switch on helper exit, ordinary logoff or updater restart; no forced-power-loss guarantee; startup requires the logged-in helper/autostart. Default-enabled power sync is independently switchable in TV settings. Legacy startup checkbox applies when sync disabled.

1. Tests first: coalesce resume notifications; cancel stale HDMI1 for sleep; retry failed sleep; canceled sleep resumes1; bounded down operation; no commands when disabled. Run failure before implementation.
2. Implement coordinator and hidden-window event mapping. Add shared TV input gate and warm-session transport; extend protocol tests for no-WoL single-shot and authenticated persistent commands.
3. Connect native events, command-triggered sleep preparation, TV settings and startup lifecycle. Review cancelled transitions and updater/logoff exclusions.
4. Run protocol, Windows integration and full suite in GitHub Actions; review branch. Release1.7.4 Windows only, retaining Android1.7.3 and cloud1.7.2. Update instructions cover manual Start-menu sleep/shutdown and physical wake tests.
