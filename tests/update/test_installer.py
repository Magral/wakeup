"""Run the actual installer script against temporary files; never launch a real helper."""
from pathlib import Path
import os
import subprocess
import tempfile

script = Path('pc-agent/AgentUpdater.cs').read_text().split('const string script = """\n', 1)[1].split('\n""";', 1)[0]
# Avoid a modal dialog in a failure-path test; file replacement and recovery remain unchanged.
script = script.replace('Add-Type -AssemblyName System.Windows.Forms', '')
script = script.replace('[System.Windows.Forms.MessageBox]::Show("Обновление не завершено. Запущена сохранённая версия. " + $problem, \'Wake Mini\') | Out-Null', 'Write-Output $problem')
for case in ('success', 'failed_launch', 'locked', 'rollback'):
    with tempfile.TemporaryDirectory() as temp:
        root = Path(temp)
        target = root / 'WakeMiniAgent.exe'
        source = root / 'staging' / 'WakeMiniAgent.exe'
        source.parent.mkdir()
        target.write_bytes(b'old helper')
        source.write_bytes(b'new helper')
        (root / 'wake.key').write_bytes(b'unchanged key')
        backup = root / 'WakeMiniAgent.exe.previous'
        if case == 'rollback':
            backup.write_bytes(b'previous helper')
            source.write_bytes(b'previous helper')
        prelude = '''
function Wait-Process { param($Id, $Timeout, $ErrorAction) }
function Get-Process { param($Id, $ErrorAction) }
function Start-Sleep { param($Seconds, $Milliseconds) }
function Start-Process { param($FilePath, $WorkingDirectory, [switch]$PassThru)
    if ($PassThru) { [pscustomobject]@{HasExited = ($env:WAKE_TEST_FAIL -eq '1')} }
}
'''
        if case == 'locked':
            prelude += "$lock = [IO.File]::Open($env:WAKE_UPDATE_TARGET, 'Open', 'ReadWrite', 'None')\n"
        env = dict(os.environ, WAKE_UPDATE_TARGET=str(target), WAKE_UPDATE_SOURCE=str(source),
                   WAKE_UPDATE_STAGING=str(source.parent), WAKE_UPDATE_PID='12345',
                   WAKE_UPDATE_ROLLBACK='1' if case == 'rollback' else '0',
                   WAKE_TEST_FAIL='1' if case == 'failed_launch' else '0')
        runner = root / 'test.ps1'
        runner.write_text(prelude + script, encoding='utf-8-sig')
        subprocess.run(['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', str(runner)],
                       env=env, check=True, timeout=30)
        expected = b'new helper' if case == 'success' else b'previous helper' if case == 'rollback' else b'old helper'
        assert target.read_bytes() == expected, case
        assert (root / 'wake.key').read_bytes() == b'unchanged key', case
        if case in ('success', 'failed_launch'):
            assert backup.read_bytes() == b'old helper', case
        print('Installer:', case, 'OK')
