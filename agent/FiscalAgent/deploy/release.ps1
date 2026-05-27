# Ruleaza din radacina proiectului: .\agent\FiscalAgent\deploy\release.ps1
# Construieste un zip de distributie gata de urcat pe GitHub Releases.

$ProjectDir = Join-Path $PSScriptRoot ".."
$Version    = (Get-Content (Join-Path $ProjectDir "version.txt") -Raw).Trim()
$OutDir     = Join-Path $PSScriptRoot "..\..\..\..\releases\FiscalAgent-v$Version"
$ZipPath    = "$OutDir.zip"

Write-Host "Building FiscalAgent v$Version..." -ForegroundColor Cyan

# Publica self-contained win-x64
dotnet publish $ProjectDir -c Release -r win-x64 --self-contained true -o $OutDir
if ($LASTEXITCODE -ne 0) { Write-Host "EROARE: publish esuat." -ForegroundColor Red; exit 1 }

# Creeaza zip
if (Test-Path $ZipPath) { Remove-Item $ZipPath -Force }
Compress-Archive -Path "$OutDir\*" -DestinationPath $ZipPath -CompressionLevel Optimal
Write-Host ""
Write-Host "Zip creat: $ZipPath" -ForegroundColor Green
Write-Host ""
Write-Host "Pasii urmatori:" -ForegroundColor Yellow
Write-Host "  1. Incarca $ZipPath pe GitHub Releases (tag: v$Version)"
Write-Host "  2. Copiaza URL-ul zip-ului de pe GitHub"
Write-Host "  3. Seteaza pe server:"
Write-Host "       AGENT_VERSION=$Version"
Write-Host "       AGENT_DOWNLOAD_URL=<url zip de pe GitHub>"
Write-Host "  4. Restart backend"
Write-Host ""
Write-Host "Agentii se vor actualiza automat la urmatoarea verificare (03:00 sau boot)." -ForegroundColor Cyan
