#requires -Version 7
# Downloads the unpacking tools WinBox ships with into .tools\: 7-Zip (NSIS installers, MSI cabinets, 7z, zip,
# RAR...) and innounp (Inno Setup 1.2 to 7.1). Each download is checked against its SHA-256. The build copies
# them next to WinBox in tools\, and the release zip carries them.
#   ./scripts/get-tools.ps1
$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
$tools = Join-Path $repo '.tools'
$downloads = Join-Path $tools 'downloads'
New-Item -ItemType Directory $downloads -Force | Out-Null

function Get-Checked([string] $url, [string] $sha256) {
    $file = Join-Path $downloads (Split-Path $url -Leaf)
    if (-not (Test-Path $file) -or (Get-FileHash $file -Algorithm SHA256).Hash -ne $sha256) {
        Invoke-WebRequest $url -OutFile $file
        $actual = (Get-FileHash $file -Algorithm SHA256).Hash
        if ($actual -ne $sha256) { Remove-Item $file; throw "$url has SHA-256 $actual, expected $sha256" }
    }
    $file
}

# 7-Zip 26.05: 7zr.exe opens the x64 package, which is itself a 7z archive, so nothing is installed.
$7zr = Get-Checked 'https://github.com/ip7z/7zip/releases/download/26.05/7zr.exe' '9BF582B894DFA665F852D7E9112B02290A1C303C2D95A21FCDC41FFCB03143B7'
$7zPackage = Get-Checked 'https://github.com/ip7z/7zip/releases/download/26.05/7z2605-x64.exe' 'FD99E86F57D8F429EF5ED045BF98315AF962E2309B0F1BA87A147A44C71647D0'
$unpacked = Join-Path $downloads '7z2605-x64'
& $7zr x $7zPackage "-o$unpacked" -y | Out-Null
if ($LASTEXITCODE) { throw "unpacking $7zPackage failed ($LASTEXITCODE)" }
$7zip = New-Item -ItemType Directory (Join-Path $tools '7zip') -Force
Copy-Item (Join-Path $unpacked '7z.exe'), (Join-Path $unpacked '7z.dll'), (Join-Path $unpacked 'License.txt') $7zip

# innounp 2.71 (InnoUnpacker 2.2.13), MIT.
$innounpZip = Get-Checked 'https://github.com/jrathlev/InnoUnpacker-Windows-GUI/releases/download/ui_2_2_13/innounp-2.zip' 'F8E45C9676BEBC7D7570108AB572673B541FCF7A7D3D24A04319AC6B5315B4E8'
$innounp = New-Item -ItemType Directory (Join-Path $tools 'innounp') -Force
Expand-Archive $innounpZip $innounp -Force
Invoke-WebRequest 'https://raw.githubusercontent.com/jrathlev/InnoUnpacker-Windows-GUI/master/LICENSE' -OutFile (Join-Path $innounp 'LICENSE.txt')

"7-Zip:   $(Join-Path $7zip '7z.exe')"
"innounp: $(Join-Path $innounp 'innounp.exe')"
