#requires -Version 7
# Builds the release zip: ./scripts/release.ps1 [-Innoextract <path to innoextract.exe>]
# Publishes WinBox and its box payloads as native code (Native AOT; needs the Visual Studio Build Tools' C++
# tools), bundles innoextract and its licences in tools\, and writes artifacts\release\WinBox-<version>-win-x64.zip
# with its SHA-256. The debug symbols stay out of the zip, in artifacts\release\symbols\.
param([string] $Innoextract = (Get-Command innoextract -ErrorAction SilentlyContinue).Source)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
$version = (Select-Xml -Path (Join-Path $repo 'Directory.Build.props') -XPath '//Version').Node.InnerText
if (-not $Innoextract) { throw 'innoextract not found: pass -Innoextract <path to innoextract.exe>' }
# winget installs a link; the licences sit next to the real exe.
$innoDir = Split-Path ((Get-Item $Innoextract).Target ?? $Innoextract)

$out = Join-Path $repo 'artifacts\release'
$stage = Join-Path $out 'WinBox'
$symbols = Join-Path $out 'symbols'
$zip = Join-Path $out "WinBox-$version-win-x64.zip"
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue

dotnet publish (Join-Path $repo 'src\WinBox.App\WinBox.App.csproj') -c Release -o $stage -nologo
if ($LASTEXITCODE) { throw "dotnet publish failed ($LASTEXITCODE)" }

foreach ($pdb in Get-ChildItem $stage -Recurse -Filter *.pdb) {
    $target = Join-Path $symbols ([IO.Path]::GetRelativePath($stage, $pdb.FullName))
    New-Item -ItemType Directory (Split-Path $target) -Force | Out-Null
    Move-Item $pdb.FullName $target
}

$tools = New-Item -ItemType Directory (Join-Path $stage 'tools')
Copy-Item (Join-Path $innoDir 'innoextract.exe') $tools
Copy-Item (Join-Path $innoDir 'license') (Join-Path $tools 'innoextract-license') -Recurse

Compress-Archive -Path $stage -DestinationPath $zip
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $(Split-Path $zip -Leaf)" | Set-Content "$zip.sha256" -Encoding ascii
'{0} ({1:N1} MB)' -f $zip, ((Get-Item $zip).Length / 1MB)
"sha256 $hash"
