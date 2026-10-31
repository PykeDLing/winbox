#requires -Version 7
# Builds the release zip: ./scripts/release.ps1 [-Innoextract <path to innoextract.exe>]
# Publishes WinBox framework-dependent with the box payloads, bundles innoextract and its licences in tools\,
# and writes artifacts\release\WinBox-<version>-win-x64.zip with its SHA-256.
param([string] $Innoextract = (Get-Command innoextract -ErrorAction SilentlyContinue).Source)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
$version = (Select-Xml -Path (Join-Path $repo 'Directory.Build.props') -XPath '//Version').Node.InnerText
if (-not $Innoextract) { throw 'innoextract not found: pass -Innoextract <path to innoextract.exe>' }
# winget installs a link; the licences sit next to the real exe.
$innoDir = Split-Path ((Get-Item $Innoextract).Target ?? $Innoextract)

$out = Join-Path $repo 'artifacts\release'
$stage = Join-Path $out 'WinBox'
$zip = Join-Path $out "WinBox-$version-win-x64.zip"
# The payload folders keep whatever older builds left there, and every file in them is shipped.
foreach ($dir in 'artifacts\boxhost\Release', 'artifacts\boxshim\Release', 'artifacts\release') {
    Remove-Item (Join-Path $repo $dir) -Recurse -Force -ErrorAction SilentlyContinue
}

dotnet publish (Join-Path $repo 'src\WinBox.App\WinBox.App.csproj') -c Release -o $stage -nologo
if ($LASTEXITCODE) { throw "dotnet publish failed ($LASTEXITCODE)" }

$tools = New-Item -ItemType Directory (Join-Path $stage 'tools')
Copy-Item (Join-Path $innoDir 'innoextract.exe') $tools
Copy-Item (Join-Path $innoDir 'license') (Join-Path $tools 'innoextract-license') -Recurse

Compress-Archive -Path $stage -DestinationPath $zip
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $(Split-Path $zip -Leaf)" | Set-Content "$zip.sha256" -Encoding ascii
'{0} ({1:N1} MB)' -f $zip, ((Get-Item $zip).Length / 1MB)
"sha256 $hash"
