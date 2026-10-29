# Run once on your own Windows PC. No private key or password is printed.
$ErrorActionPreference = 'Stop'
$repo = 'Magral/wakeup'
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Write-Host 'Install GitHub CLI first: winget install --id GitHub.cli --exact'
    Write-Host 'Then open a new PowerShell window and run this script again.'
    exit 1
}
$keytool = (Get-Command keytool -ErrorAction SilentlyContinue).Source
if (-not $keytool) { $keytool = Join-Path $env:ProgramFiles 'Android\Android Studio\jbr\bin\keytool.exe' }
if (-not (Test-Path $keytool)) { throw 'Java keytool is required. Install Android Studio or a Java JDK.' }
& gh auth status 2>$null
if ($LASTEXITCODE -ne 0) { & gh auth login --hostname github.com --web --git-protocol https }
if ($LASTEXITCODE -ne 0) { throw 'GitHub sign-in failed.' }
$signingDir = Join-Path $env:LOCALAPPDATA 'WakeMiniSigning'
New-Item -ItemType Directory -Force -Path $signingDir | Out-Null
$store = Join-Path $signingDir 'wakemini.jks'
$protectedPassword = Join-Path $signingDir 'password.dpapi'
if (Test-Path $store) {
    if (-not (Test-Path $protectedPassword)) { throw 'Existing keystore found without its password file. It was not overwritten.' }
    $secure = (Get-Content $protectedPassword -Raw).Trim() | ConvertTo-SecureString
    $password = [System.Net.NetworkCredential]::new('', $secure).Password
} else {
    $bytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $rng.GetBytes($bytes); $rng.Dispose()
    $password = [Convert]::ToBase64String($bytes)
    $env:WAKEMINI_SIGN_PASSWORD = $password
    try {
        & $keytool -genkeypair -keystore $store -alias wakemini -storetype JKS -keyalg RSA -keysize 3072 -validity 10000 -dname 'CN=Wake Mini' -storepass:env WAKEMINI_SIGN_PASSWORD -keypass:env WAKEMINI_SIGN_PASSWORD
        if ($LASTEXITCODE -ne 0) { throw 'Keystore generation failed.' }
        ConvertTo-SecureString $password -AsPlainText -Force | ConvertFrom-SecureString | Set-Content $protectedPassword
    } finally { Remove-Item Env:WAKEMINI_SIGN_PASSWORD -ErrorAction SilentlyContinue }
}
$values = @{
    ANDROID_KEYSTORE_BASE64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($store))
    ANDROID_KEYSTORE_PASSWORD = $password
    ANDROID_KEY_ALIAS = 'wakemini'
    ANDROID_KEY_PASSWORD = $password
}
foreach ($name in $values.Keys) {
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = (Get-Command gh).Source
    $start.Arguments = "secret set $name --repo $repo"
    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $true
    $process = [System.Diagnostics.Process]::Start($start)
    $process.StandardInput.Write($values[$name])
    $process.StandardInput.Close()
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "Could not save secret $name. Verify access to the repository." }
}
$values.Clear(); $password = $null
& gh workflow run publish-release.yml --repo $repo --ref main
if ($LASTEXITCODE -ne 0) { throw 'Signing secrets saved, but workflow could not be started. Run Publish Wake Mini release in Actions.' }
Write-Host "Permanent signing configured. Keep $signingDir on this PC. The signed APK will appear in release v1.3.0."
