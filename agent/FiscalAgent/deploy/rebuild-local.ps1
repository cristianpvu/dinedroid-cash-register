#Requires -RunAsAdministrator

$ServiceName = "FiscalAgent"
$ProjectDir  = "C:\Users\cristi\Desktop\cash-register\agent\FiscalAgent"
$InstallDir  = "C:\FiscalAgent"
$BuildOut    = "C:\FiscalAgent-dist"

Write-Host "== Build ==" -ForegroundColor Cyan
dotnet publish "$ProjectDir\FiscalAgent.csproj" -c Release -r win-x64 --self-contained true -o "$BuildOut" --nologo -v quiet
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build esuat!" -ForegroundColor Red
    exit 1
}
Write-Host "Build OK" -ForegroundColor Green

Write-Host "== Opresc serviciul ==" -ForegroundColor Cyan
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc -ne $null) {
    Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 3
    Write-Host "Serviciu oprit" -ForegroundColor Green
} else {
    Write-Host "Serviciul nu exista inca" -ForegroundColor Yellow
}

Write-Host "== Copiez fisierele ==" -ForegroundColor Cyan
if (-not (Test-Path $InstallDir)) {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
}
Copy-Item "$BuildOut\*" $InstallDir -Recurse -Force
Write-Host "Copiere OK" -ForegroundColor Green

Write-Host "== Pornesc serviciul ==" -ForegroundColor Cyan
if ($svc -ne $null) {
    Start-Service $ServiceName
    Start-Sleep -Seconds 3
    $status = (Get-Service -Name $ServiceName).Status
    if ($status -eq "Running") {
        Write-Host "Serviciu pornit cu succes. Stare: $status" -ForegroundColor Green
    } else {
        Write-Host "Serviciul nu a pornit. Stare: $status" -ForegroundColor Red
        exit 1
    }
} else {
    Write-Host "Ruleaza setup.ps1 pentru prima instalare a serviciului." -ForegroundColor Yellow
}

Write-Host "== Gata ==" -ForegroundColor Green
