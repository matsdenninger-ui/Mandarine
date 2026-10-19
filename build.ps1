# Baut Zytrus als einzelne, eigenständige .exe (kein installiertes .NET auf dem Ziel-PC nötig).
# Aufruf:  .\build.ps1            (x64)
#          .\build.ps1 win-arm64  (Snapdragon / ARM)
param([string]$Runtime = "win-x64")
$ErrorActionPreference = "Stop"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "Das .NET 10 SDK fehlt. Installieren mit:  winget install Microsoft.DotNet.SDK.10" -ForegroundColor Yellow
    exit 1
}

$out = Join-Path $PSScriptRoot "publish\$Runtime"
dotnet publish (Join-Path $PSScriptRoot "src\Zytrus\Zytrus.csproj") `
    -c Release -r $Runtime --self-contained true -p:PublishSingleFile=true -o $out

Write-Host ""
Write-Host "Fertig: $out\Zytrus.exe" -ForegroundColor Green
