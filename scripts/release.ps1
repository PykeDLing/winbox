#requires -Version 7
# Builds the release zip: ./scripts/release.ps1 (run ./scripts/get-tools.ps1 once before)
# Publishes WinBox and its box payloads as native code (Native AOT; needs the Visual Studio Build Tools' C++
# tools) with the unpacking tools (7-Zip, innounp) and their licences in tools\, and writes
# artifacts\release\WinBox-<version>-win-x64.zip with its SHA-256. The debug symbols stay out of the zip, in
# artifacts\release\symbols\.
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
$version = (Select-Xml -Path (Join-Path $repo 'Directory.Build.props') -XPath '//Version').Node.InnerText
foreach ($tool in '.tools\7zip\7z.exe', '.tools\7zip\7z.dll', '.tools\innounp\innounp.exe', '.tools\innounp\LICENSE.txt') {
    if (-not (Test-Path (Join-Path $repo $tool))) { throw "$tool is missing: run ./scripts/get-tools.ps1" }
}

$out = Join-Path $repo 'artifacts\release'
$stage = Join-Path $out 'WinBox'
$symbols = Join-Path $out 'symbols'
$zip = Join-Path $out "WinBox-$version-win-x64.zip"
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue

dotnet publish (Join-Path $repo 'src\WinBox.App\WinBox.App.csproj') -c Release -o $stage -nologo
if ($LASTEXITCODE) { throw "dotnet publish failed ($LASTEXITCODE)" }
# The publish puts the unpacking tools in tools\ (build\UnpackTools.targets).
if (-not (Test-Path (Join-Path $stage 'tools\innounp\innounp.exe'))) { throw 'the publish left out tools\' }

foreach ($pdb in Get-ChildItem $stage -Recurse -Filter *.pdb) {
    $target = Join-Path $symbols ([IO.Path]::GetRelativePath($stage, $pdb.FullName))
    New-Item -ItemType Directory (Split-Path $target) -Force | Out-Null
    Move-Item $pdb.FullName $target
}

Compress-Archive -Path $stage -DestinationPath $zip
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $(Split-Path $zip -Leaf)" | Set-Content "$zip.sha256" -Encoding ascii
'{0} ({1:N1} MB)' -f $zip, ((Get-Item $zip).Length / 1MB)
"sha256 $hash"
