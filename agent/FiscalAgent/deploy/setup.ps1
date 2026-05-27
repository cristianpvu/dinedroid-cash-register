#Requires -RunAsAdministrator

$ServiceName = "FiscalAgent"
$InstallDir  = "C:\FiscalAgent"
$ExePath     = "$InstallDir\FiscalAgent.exe"
$ConfigPath  = "$InstallDir\appsettings.local.json"
$SourceDir   = $PSScriptRoot

Write-Host ""
Write-Host "================================================" -ForegroundColor Cyan
Write-Host "   DineDroid FiscalAgent - Setup & Instalare   " -ForegroundColor Cyan
Write-Host "================================================" -ForegroundColor Cyan
Write-Host ""

$RestaurantId = Read-Host "RestaurantId (numarul restaurantului din sistem)"
$Token        = Read-Host "Token (API key primit din backend)"

if ([string]::IsNullOrWhiteSpace($RestaurantId) -or [string]::IsNullOrWhiteSpace($Token)) {
    Write-Host "EROARE: RestaurantId si Token sunt obligatorii." -ForegroundColor Red
    Read-Host "Apasa Enter pentru a inchide"
    exit 1
}

# Opreste si sterge serviciul existent daca e instalat
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Write-Host ""
    Write-Host "Gasit instalare anterioara. Opresc si reinstalез..." -ForegroundColor Yellow
    Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 1
}

# Copiaza fisierele in directorul de instalare
Write-Host ""
Write-Host "Copiez fisierele in $InstallDir ..." -ForegroundColor White
if (-not (Test-Path $InstallDir)) {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
}
Copy-Item "$SourceDir\*" $InstallDir -Recurse -Force

# Scrie configuratia specifica acestui restaurant
Write-Host "Scriu configuratia restaurantului..." -ForegroundColor White
$config = @"
{
  "Agent": {
    "RestaurantId": "$RestaurantId",
    "Backend": {
      "Enabled": true,
      "BaseUrl": "https://api.dinedroid.com",
      "Token": "$Token",
      "StreamPath": "/agent/stream?restaurantId=$RestaurantId",
      "ResultPath": "/agent/jobs/{jobId}/result"
    }
  }
}
"@
[System.IO.File]::WriteAllText($ConfigPath, $config, (New-Object System.Text.UTF8Encoding $false))

# Instaleaza ca Windows Service cu auto-start la boot
Write-Host "Instalez Windows Service..." -ForegroundColor White
New-Service -Name $ServiceName `
    -BinaryPathName $ExePath `
    -DisplayName "FiscalAgent - DineDroid" `
    -Description "DineDroid fiscal printing agent - bonuri fiscale" `
    -StartupType Automatic | Out-Null

Start-Service $ServiceName
Start-Sleep -Seconds 3

$svc = Get-Service -Name $ServiceName
Write-Host ""
if ($svc.Status -eq "Running") {
    Write-Host "================================================" -ForegroundColor Green
    Write-Host "   FiscalAgent instalat si pornit cu succes!   " -ForegroundColor Green
    Write-Host "================================================" -ForegroundColor Green
    Write-Host ""
    Write-Host "Serviciul porneste automat la fiecare boot." -ForegroundColor White
    Write-Host "RestaurantId : $RestaurantId" -ForegroundColor Gray
    Write-Host "InstallDir   : $InstallDir" -ForegroundColor Gray
} else {
    Write-Host "EROARE: Serviciul nu s-a pornit. Stare: $($svc.Status)" -ForegroundColor Red
    Write-Host "Verifica Event Viewer > Windows Logs > Application pentru detalii." -ForegroundColor Yellow
}

Write-Host ""
Read-Host "Apasa Enter pentru a inchide"
