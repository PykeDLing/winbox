#requires -Version 7
# Runs the WinBox developer CLI from source: ./scripts/winbox.ps1 <command> [args]
# Development builds keep their data in <repo>\.data, standing in for the install directory.
param([Parameter(ValueFromRemainingArguments)] [string[]] $CliArgs)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
$env:WINBOX_DATA_ROOT = Join-Path $repo '.data'
$project = Join-Path $repo 'src\WinBox.Cli\WinBox.Cli.csproj'
dotnet run --project $project --no-build -- @CliArgs
exit $LASTEXITCODE
