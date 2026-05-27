#Requires -RunAsAdministrator

$ServiceName = "FiscalAgent"
$InstallDir  = "C:\FiscalAgent"
$VersionUrl  = "https://api.dinedroid.com/agent/version"
$LogFile     = "$InstallDir\data\updater.log"

function Write-Log($msg) {
    $line = "$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')  $msg"
    Write-Host $line
    try { Add-Content -Path $LogFile -Value $line -Encoding UTF8 -ErrorAction SilentlyContinue } catch {}
}

New-Item -ItemType Directory -Path (Split-Path $LogFile) -Force | Out-Null

Write-Log "=== Update check ==="

# Citeste versiunea instalata
$localVersion = "0.0.0"
$versionFile  = "$InstallDir\version.txt"
if (Test-Path $versionFile) { $localVersion = (Get-Content $versionFile -Raw).Trim() }
Write-Log "Versiune locala: $localVersion"

# Verifica versiunea disponibila pe backend
try {
    $remote        = Invoke-RestMethod -Uri $VersionUrl -TimeoutSec 15 -ErrorAction Stop
    $remoteVersion = $remote.version.Trim()
    $downloadUrl   = $remote.url
    Write-Log "Versiune disponibila: $remoteVersion"
} catch {
    Write-Log "WARN: Nu am putut verifica versiunea: $_"
    exit 0
}

if ([string]::IsNullOrWhiteSpace($downloadUrl)) {
    Write-Log "WARN: Niciun URL de download configurat pe backend."
    exit 0
}

if ([version]$remoteVersion -le [version]$localVersion) {
    Write-Log "Deja la versiunea curenta. Nimic de facut."
    exit 0
}

Write-Log "Update disponibil: $localVersion -> $remoteVersion. Descarc..."

# Descarca zip-ul in temp
$zipPath = Join-Path $env:TEMP "FiscalAgent-$remoteVersion.zip"
$tempDir = Join-Path $env:TEMP "FiscalAgent-$remoteVersion"
try {
    Invoke-WebRequest -Uri $downloadUrl -OutFile $zipPath -TimeoutSec 180 -ErrorAction Stop
    Write-Log "Descarcat: $zipPath"
} catch {
    Write-Log "EROARE: Download esuat: $_"
    exit 1
}

# Extrage
if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force }
Expand-Archive -Path $zipPath -DestinationPath $tempDir -Force
Remove-Item $zipPath -Force -ErrorAction SilentlyContinue
Write-Log "Extras in $tempDir"

# Opreste serviciul
Write-Log "Opresc serviciul..."
Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 3

# Copiaza fisierele noi — appsettings.local.json ramane intact (nu e in zip)
Write-Log "Instalez fisierele noi..."
Copy-Item "$tempDir\*" $InstallDir -Recurse -Force
Remove-Item $tempDir -Recurse -Force -ErrorAction SilentlyContinue

# Porneste serviciul
Write-Log "Pornesc serviciul..."
Start-Service $ServiceName
Start-Sleep -Seconds 3

$status = (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue).Status
Write-Log "Stare serviciu: $status"

if ($status -eq "Running") {
    Write-Log "Update la $remoteVersion finalizat cu succes."
} else {
    Write-Log "EROARE: Serviciul nu a pornit dupa update."
    exit 1
}
