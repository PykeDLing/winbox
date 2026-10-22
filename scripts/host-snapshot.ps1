#requires -Version 7
<#
.SYNOPSIS
    Snapshot the parts of the host a sandbox diagnostic run might change, then diff or restore them.

.DESCRIPTION
    backup  : copies sensitive shared stores and exports registry keys into -Dir, plus an inventory of
              places apps commonly write to (HKCU\Software keys, AppData folders, shortcuts, tasks).
    diff    : compares the current host with the inventory in -Dir and prints what appeared or vanished.
    restore : puts the backed-up stores and registry keys back exactly as they were.

    Used around permissive (allow-and-record) MXC runs, which can write to the real system.
#>
param(
    [Parameter(Mandatory)] [ValidateSet('backup', 'diff', 'restore')] [string] $Mode,
    [Parameter(Mandatory)] [string] $Dir
)
$ErrorActionPreference = 'Stop'
# Shells started by Claude Code see the registry and AppData through their own overlay: deletes and restores
# land in the overlay and diffs read it, so the real host is never checked. Run this via an explorer-launched .cmd.
if ($env:CLAUDECODE) { throw 'host-snapshot.ps1 must run outside Claude Code (start it through explorer.exe).' }

# Shared stores that already hold the user's own data and must come back byte-for-byte.
$fileStores = [ordered]@{
    'utForpc' = Join-Path $env:APPDATA 'utForpc'
    'Alibaba-Public' = Join-Path $env:PUBLIC 'Documents\Alibaba'
}
$regKeys = [ordered]@{
    'UTForPC' = 'HKCU\Software\UTForPC'
    'Qianwen' = 'HKCU\Software\Qianwen'
    'Classes' = 'HKCU\Software\Classes'
    'Run' = 'HKCU\Software\Microsoft\Windows\CurrentVersion\Run'
    'Uninstall' = 'HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall'
    'FileExts' = 'HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts'
}

function Get-Inventory {
    $names = { param($path) if (Test-Path $path) { Get-ChildItem $path -Force -ErrorAction SilentlyContinue | ForEach-Object Name | Sort-Object } else { @() } }
    [ordered]@{
        HkcuSoftware = & $names 'HKCU:\Software'
        Roaming = & $names $env:APPDATA
        Local = & $names $env:LOCALAPPDATA
        LocalPrograms = & $names (Join-Path $env:LOCALAPPDATA 'Programs')
        LocalProgramsCommon = & $names (Join-Path $env:LOCALAPPDATA 'Programs\Common')
        PublicDocuments = & $names (Join-Path $env:PUBLIC 'Documents')
        StartMenu = Get-ChildItem ([Environment]::GetFolderPath('Programs')), ([Environment]::GetFolderPath('CommonPrograms')) -Recurse -File -ErrorAction SilentlyContinue | ForEach-Object FullName | Sort-Object
        Desktop = Get-ChildItem ([Environment]::GetFolderPath('Desktop')), ([Environment]::GetFolderPath('CommonDesktopDirectory')) -File -ErrorAction SilentlyContinue | ForEach-Object FullName | Sort-Object
        Startup = & $names ([Environment]::GetFolderPath('Startup'))
        RunValues = (Get-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run').GetValueNames() | Sort-Object
        Tasks = (schtasks /query /fo csv /nh | ConvertFrom-Csv -Header TaskName, Next, Status).TaskName | Sort-Object -Unique
        Services = Get-Service | ForEach-Object Name | Sort-Object
    }
}

switch ($Mode) {
    'backup' {
        New-Item -ItemType Directory -Force $Dir | Out-Null
        foreach ($name in $fileStores.Keys) {
            $src = $fileStores[$name]
            if (Test-Path $src) { robocopy $src (Join-Path $Dir "files\$name") /E /COPY:DAT /DCOPY:DAT /R:1 /W:1 /NFL /NDL /NJH /NJS | Out-Null }
        }
        New-Item -ItemType Directory -Force (Join-Path $Dir 'reg') | Out-Null
        foreach ($name in $regKeys.Keys) {
            reg query $regKeys[$name] *> $null
            if ($LASTEXITCODE -eq 0) { reg export $regKeys[$name] (Join-Path $Dir "reg\$name.reg") /y | Out-Null }
        }
        Get-Inventory | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $Dir 'inventory.json')
        "backup written to $Dir"
        Get-ChildItem $Dir -Recurse -File | Measure-Object Length -Sum | ForEach-Object { "  files: $($_.Count), bytes: $($_.Sum)" }
    }
    'diff' {
        $before = Get-Content (Join-Path $Dir 'inventory.json') -Raw | ConvertFrom-Json -AsHashtable
        $after = Get-Inventory
        foreach ($key in $after.Keys) {
            $added = @($after[$key] | Where-Object { $_ -notin $before[$key] })
            $removed = @($before[$key] | Where-Object { $_ -notin $after[$key] })
            foreach ($a in $added) { "+ [$key] $a" }
            foreach ($r in $removed) { "- [$key] $r" }
        }
        foreach ($name in $fileStores.Keys) {
            $src = $fileStores[$name]; $bak = Join-Path $Dir "files\$name"
            if (-not (Test-Path $src) -and -not (Test-Path $bak)) { continue }
            $now = if (Test-Path $src) { Get-ChildItem $src -Recurse -File | ForEach-Object { "$($_.FullName.Substring($src.Length))|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" } } else { @() }
            $was = if (Test-Path $bak) { Get-ChildItem $bak -Recurse -File | ForEach-Object { "$($_.FullName.Substring($bak.Length))|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)" } } else { @() }
            foreach ($c in (Compare-Object @($was) @($now))) { "$($c.SideIndicator) [$name] $($c.InputObject)" }
        }
        "diff done"
    }
    'restore' {
        foreach ($name in $fileStores.Keys) {
            $dst = $fileStores[$name]; $bak = Join-Path $Dir "files\$name"
            if (Test-Path $bak) { robocopy $bak $dst /MIR /COPY:DAT /DCOPY:DAT /R:1 /W:1 /NFL /NDL /NJH /NJS | Out-Null; "restored $dst" }
            elseif (Test-Path $dst) { Remove-Item $dst -Recurse -Force; "removed new $dst" }
        }
        foreach ($name in $regKeys.Keys) {
            $file = Join-Path $Dir "reg\$name.reg"
            if (Test-Path $file) {
                reg delete $regKeys[$name] /f *> $null
                reg import $file *> $null
                "restored $($regKeys[$name]) (exit $LASTEXITCODE)"
            }
        }
    }
}
