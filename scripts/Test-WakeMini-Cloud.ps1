$ErrorActionPreference = 'Stop'
$wakeFunctionUrl = Read-Host 'URL вашей облачной функции (https://functions.yandexcloud.net/...)'
$wakeUri = [Uri]$wakeFunctionUrl
if ($wakeUri.Scheme -ne 'https' -or $wakeUri.Host -ne 'functions.yandexcloud.net' -or $wakeUri.Query) {
    throw 'Нужна прямая HTTPS-ссылка функции functions.yandexcloud.net без параметров'
}
$wakeKeyFile = Read-Host 'Полный путь к прежнему wake.key на этом ПК'
$wakeSecret = (Get-Content -LiteralPath $wakeKeyFile -Raw).Trim()
if ($wakeSecret -notmatch '^[0-9a-fA-F]{64}$') { throw 'В файле не найден ключ Wake Mini' }
try {
    Write-Host 'Проверяю только связь; команды питания не отправляются.'
    $wakeFirst = Invoke-RestMethod -Uri ($wakeFunctionUrl.TrimEnd('/') + '?wakemini_diag=1') -Headers @{'X-WakeMini-Key'=$wakeSecret} -TimeoutSec 15
    $wakeFirst | ConvertTo-Json -Depth 12
    Read-Host 'Теперь запустите проблемный сценарий Алисы, дождитесь результата и нажмите Enter'
    $wakeSecond = Invoke-RestMethod -Uri ($wakeFunctionUrl.TrimEnd('/') + '?wakemini_diag=1') -Headers @{'X-WakeMini-Key'=$wakeSecret} -TimeoutSec 15
    @{'before'=$wakeFirst; 'after'=$wakeSecond} | ConvertTo-Json -Depth 15
} finally { $wakeSecret = $null }
