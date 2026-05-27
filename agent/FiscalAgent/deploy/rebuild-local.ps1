#Requires -RunAsAdministrator
# Rebuilds FiscalAgent from source and redeploys to the local Windows Service.
# Run from anywhere — path-urile sunt hardcodate.

$ServiceName = "FiscalAgent"
$ProjectDir  = "C:\Users\cristi\Desktop\cash-register\agent\FiscalAgent"
$InstallDir  = "C:\FiscalAgent"
$BuildOut    = "C:\FiscalAgent-dist"

function Write-Step($msg) { Write-Host "`n>> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "   OK: $msg" -ForegroundColor Green }
function Write-Err($msg)  { Write-Host "   EROARE: $msg" -ForegroundColor Red; exit 1 }

Write-Host ""
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "   FiscalAgent - Rebuild & Redeploy       " -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

# 1. Build
Write-Step "dotnet publish..."
dotnet publish "$ProjectDir\FiscalAgent.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -o "$BuildOut" `
    --nologo -v quiet

if ($LASTEXITCODE -ne 0) { Write-Err "Build esuat (exit code $LASTEXITCODE)" }
Write-Ok "Build reusit -> $BuildOut"

# 2. Stop service
Write-Step "Opresc serviciul $ServiceName..."
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -eq "Running") {
        Stop-Service $ServiceName -Force
        Start-Sleep -Seconds 3
    }
    Write-Ok "Serviciu oprit"
} else {
    Write-Host "   INFO: Serviciul nu exista inca — va fi instalat manual dupa build." -ForegroundColor Yellow
}

# 3. Copiaza fisierele noi (appsettings.local.json si data/ raman intacte)
Write-Step "Copiez fisierele in $InstallDir..."
if (-not (Test-Path $InstallDir)) { New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null }
Copy-Item "$BuildOut\*" $InstallDir -Recurse -Force
Write-Ok "Fisiere copiate"

# 4. Porneste serviciul
if ($svc) {
    Write-Step "Pornesc serviciul $ServiceName..."
    Start-Service $ServiceName
    Start-Sleep -Seconds 3

    $status = (Get-Service -Name $ServiceName).Status
    if ($status -eq "Running") {
        Write-Ok "Serviciu pornit. Stare: $status"
    } else {
        Write-Err "Serviciul nu a pornit. Stare: $status — verifica Event Viewer."
    }
} else {
    Write-Host "`n   INFO: Serviciul nu e instalat. Ruleaza setup.ps1 pentru prima instalare." -ForegroundColor Yellow
}

Write-Host ""
Write-Host "==========================================" -ForegroundColor Green
Write-Host "   Gata!                                  " -ForegroundColor Green
Write-Host "==========================================" -ForegroundColor Green
Write-Host ""
