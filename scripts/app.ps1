#requires -Version 7
# Starts the built WinBox app on development data (<repo>\.data): ./scripts/app.ps1
# Run it from Explorer or a normal terminal, not from inside an MSIX-packaged host (Claude Code's shells
# included): WinBox must run without package identity for MXC to start boxes.
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
$env:WINBOX_DATA_ROOT = Join-Path $repo '.data'
$exe = Join-Path $repo 'src\WinBox.App\bin\Debug\net10.0-windows10.0.26100.0\win-x64\WinBox.exe'
if (-not (Test-Path $exe)) { throw "build first: dotnet build src\WinBox.App" }
Start-Process $exe
