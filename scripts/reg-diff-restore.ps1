#requires -Version 7
<#
.SYNOPSIS
    Undo registry changes under one key by comparing a "before" .reg export with the live registry.
    New keys are deleted, new values are deleted, and changed or removed values are written back.
    Use -WhatIf to only list the changes.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)] [string] $BeforeReg,
    [Parameter(Mandatory)] [string] $Key,  # e.g. HKCU\Software\Classes
    # Only touch keys whose path, or values whose name/data, match this pattern; everything else changed
    # meanwhile by other software is left alone.
    [string] $Only = '.'
)
$ErrorActionPreference = 'Stop'
# See host-snapshot.ps1: inside Claude Code the registry is an overlay, so a restore there changes nothing real.
if ($env:CLAUDECODE) { throw 'reg-diff-restore.ps1 must run outside Claude Code (start it through explorer.exe).' }

function Read-RegExport([string] $path) {
    $map = [ordered]@{}
    $current = $null; $pending = $null
    foreach ($raw in [IO.File]::ReadAllLines($path)) {
        $line = $raw
        if ($pending) { $pending.Text += "`n" + $line; if (-not $line.EndsWith('\')) { $current[$pending.Name] = $pending.Text; $pending = $null }; continue }
        if ($line -match '^\[(.+)\]$') { $current = [ordered]@{}; $map[$Matches[1]] = $current; continue }
        if ($null -eq $current -or $line -eq '') { continue }
        $name = if ($line.StartsWith('@=')) { '@' } elseif ($line -match '^"((?:[^"\\]|\\.)*)"=') { $Matches[1] } else { continue }
        if ($line.EndsWith('\')) { $pending = @{ Name = $name; Text = $line } } else { $current[$name] = $line }
    }
    return $map
}

$after = Join-Path $env:TEMP ("reg-now-{0}.reg" -f [guid]::NewGuid().ToString('N'))
reg export $Key $after /y | Out-Null
$was = Read-RegExport $BeforeReg
$now = Read-RegExport $after
Remove-Item $after

$relevant = { param($k, $text) ($k -match $Only) -or ($text -match $Only) }
$newKeys = @($now.Keys | Where-Object { -not $was.Contains($_) -and (& $relevant $_ (($now[$_].Values) -join "`n")) })
# Only delete the top-most new keys; their subkeys go with them.
$topNewKeys = @($newKeys | Where-Object { $k = $_; -not ($newKeys | Where-Object { $_ -ne $k -and $k.StartsWith($_ + '\') }) })
$restoreLines = [Collections.Generic.List[string]]::new()
$deletedValues = 0
foreach ($k in $was.Keys) {
    if (-not $now.Contains($k)) {
        if (& $relevant $k (($was[$k].Values) -join "`n")) { $restoreLines.Add("[$k]"); foreach ($v in $was[$k].Values) { $restoreLines.Add($v) } }
        continue
    }
    $changed = @($was[$k].Keys | Where-Object { $now[$k][$_] -ne $was[$k][$_] -and (& $relevant $k "$($now[$k][$_])`n$($was[$k][$_])") })
    foreach ($name in @($now[$k].Keys | Where-Object { -not $was[$k].Contains($_) -and (& $relevant $k $now[$k][$_]) })) {
        $deletedValues++
        $regKey = $k -replace '^HKEY_CURRENT_USER', 'HKCU'
        if ($PSCmdlet.ShouldProcess("$k :: $name", 'delete value')) {
            if ($name -eq '@') { reg delete $regKey /ve /f | Out-Null } else { reg delete $regKey /v ($name -replace '\\(.)', '$1') /f | Out-Null }
        }
    }
    if ($changed) { $restoreLines.Add("[$k]"); foreach ($name in $changed) { $restoreLines.Add($was[$k][$name]) } }
}
foreach ($k in $topNewKeys) {
    if ($PSCmdlet.ShouldProcess($k, 'delete key')) { reg delete ($k -replace '^HKEY_CURRENT_USER', 'HKCU') /f | Out-Null }
}
"new keys: $($newKeys.Count) (deleted top-level: $($topNewKeys.Count)); new values deleted: $deletedValues; keys with values to restore: $(@($restoreLines | Where-Object { $_.StartsWith('[') }).Count)"
if ($restoreLines.Count -gt 0) {
    $file = Join-Path $env:TEMP ("reg-restore-{0}.reg" -f [guid]::NewGuid().ToString('N'))
    [IO.File]::WriteAllText($file, "Windows Registry Editor Version 5.00`r`n`r`n" + ($restoreLines -join "`r`n") + "`r`n", [Text.Encoding]::Unicode)
    if ($PSCmdlet.ShouldProcess($file, 'import restored values')) { reg import $file | Out-Null; "restored values (exit $LASTEXITCODE)" }
    else { "would import:"; $restoreLines | Select-Object -First 20 }
    Remove-Item $file
}
