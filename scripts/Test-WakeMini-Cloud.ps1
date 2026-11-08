$ErrorActionPreference = 'Stop'
$wakeFunctionUrl = Read-Host 'Cloud function URL (https://functions.yandexcloud.net/...)'
$wakeUri = [Uri]$wakeFunctionUrl
if ($wakeUri.Scheme -ne 'https' -or $wakeUri.Host -ne 'functions.yandexcloud.net' -or $wakeUri.Query) {
    throw 'Enter the direct HTTPS functions.yandexcloud.net URL without query parameters'
}
$wakeKeyFile = Read-Host 'Full path to your existing wake.key file'
$wakeSecret = (Get-Content -LiteralPath $wakeKeyFile -Raw).Trim()
if ($wakeSecret -notmatch '^[0-9a-fA-F]{64}$') { throw 'Invalid Wake Mini key file' }
try {
    Write-Host 'Read-only connection test. No power commands are sent.'
    $wakeFirst = Invoke-RestMethod -Uri ($wakeFunctionUrl.TrimEnd('/') + '?wakemini_diag=1') -Headers @{'X-WakeMini-Key'=$wakeSecret} -TimeoutSec 15
    $wakeFirst | ConvertTo-Json -Depth 12
    Read-Host 'Run the failing Alice scenario, wait for its result, then press Enter'
    $wakeSecond = Invoke-RestMethod -Uri ($wakeFunctionUrl.TrimEnd('/') + '?wakemini_diag=1') -Headers @{'X-WakeMini-Key'=$wakeSecret} -TimeoutSec 15
    @{'before'=$wakeFirst; 'after'=$wakeSecond} | ConvertTo-Json -Depth 15
} finally { $wakeSecret = $null }
