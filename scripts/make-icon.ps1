#requires -Version 7
# Renders WinBox's icon from the drawings in src\WinBox.App\Assets\Icon: ./scripts/make-icon.ps1
# Writes the app icon (src\WinBox.App\Assets\AppIcon.ico) and the logo of every box's package
# (src\WinBox.BoxHost\logo.png). Small sizes have drawings of their own, aligned to whole pixels, and each size
# is rendered from the drawing made for it or the nearest one. Microsoft Edge does the rendering: it comes with
# Windows and draws SVG at exactly the size asked for.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$repo = Resolve-Path (Join-Path $PSScriptRoot '..')
$drawings = Join-Path $repo 'src\WinBox.App\Assets\Icon'
$edge = Join-Path ${env:ProgramFiles(x86)} 'Microsoft\Edge\Application\msedge.exe'
if (-not (Test-Path $edge)) { throw "Microsoft Edge renders the icon, but it is not at $edge" }

# The sizes Windows picks from at 100-400 % display scaling, and the drawing each is rendered from.
$iconSizes = [ordered]@{
    16 = 'AppIcon-16.svg'; 20 = 'AppIcon-20.svg'; 24 = 'AppIcon-24.svg'
    30 = 'AppIcon-32.svg'; 32 = 'AppIcon-32.svg'; 36 = 'AppIcon-32.svg'; 40 = 'AppIcon-32.svg'
    48 = 'AppIcon-48.svg'; 60 = 'AppIcon-48.svg'; 64 = 'AppIcon-48.svg'; 72 = 'AppIcon-48.svg'; 80 = 'AppIcon-48.svg'
    96 = 'AppIcon.svg'; 128 = 'AppIcon.svg'; 256 = 'AppIcon.svg'
}
$iconJobs = @($iconSizes.GetEnumerator() | ForEach-Object { @{ Size = $_.Key; Drawing = $_.Value } })
# The box packages use one 50 px image for their Square44x44, Square150x150 and Store logos.
$logoJob = @{ Size = 50; Drawing = 'AppIcon-48.svg' }

$work = Join-Path ([IO.Path]::GetTempPath()) "winbox-icon-$PID"
New-Item -ItemType Directory -Force $work | Out-Null
try {
    # Every image side by side on one transparent page, so that Edge renders them all at once.
    $x = 0
    foreach ($job in $iconJobs + $logoJob) { $job.X = $x; $x += $job.Size + 8 }
    $images = foreach ($job in $iconJobs + $logoJob) {
        $source = [Uri]::new((Join-Path $drawings $job.Drawing)).AbsoluteUri
        "<img src=""$source"" style=""position:absolute;left:$($job.X)px;top:0;width:$($job.Size)px;height:$($job.Size)px"">"
    }
    $page = Join-Path $work 'sheet.html'
    Set-Content $page "<!doctype html><body style=""margin:0;background:transparent"">$($images -join '')</body>"
    $sheetFile = Join-Path $work 'sheet.png'
    $edgeArgs = @(
        '--headless', '--disable-gpu', '--hide-scrollbars', '--no-first-run', '--disable-extensions',
        '--force-device-scale-factor=1', '--force-color-profile=srgb', '--default-background-color=00000000',
        '--virtual-time-budget=5000', "--user-data-dir=""$(Join-Path $work 'profile')""",
        "--window-size=$x,256", "--screenshot=""$sheetFile""", [Uri]::new($page).AbsoluteUri)
    Start-Process $edge -ArgumentList $edgeArgs -Wait -WindowStyle Hidden
    if (-not (Test-Path $sheetFile)) { throw 'Edge did not render the icon' }

    $sheet = [System.Drawing.Bitmap]::new($sheetFile)
    try {
        function Png($job) {
            $image = $sheet.Clone([System.Drawing.Rectangle]::new($job.X, 0, $job.Size, $job.Size),
                [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
            try {
                $stream = [IO.MemoryStream]::new()
                $image.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
                , $stream.ToArray()
            }
            finally { $image.Dispose() }
        }

        # An .ico is a directory of images. Each is stored as PNG, which Windows reads at any size.
        $entries = foreach ($job in $iconJobs) { @{ Size = $job.Size; Png = (Png $job) } }
        $ico = [IO.MemoryStream]::new()
        $writer = [IO.BinaryWriter]::new($ico)
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$entries.Count)
        $offset = 6 + 16 * $entries.Count
        foreach ($entry in $entries) {
            $side = [byte]($entry.Size % 256) # 0 stands for 256
            $writer.Write($side); $writer.Write($side); $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$entry.Png.Length); $writer.Write([uint32]$offset)
            $offset += $entry.Png.Length
        }
        foreach ($entry in $entries) { $writer.Write($entry.Png) }
        $writer.Flush()
        [IO.File]::WriteAllBytes((Join-Path $repo 'src\WinBox.App\Assets\AppIcon.ico'), $ico.ToArray())
        [IO.File]::WriteAllBytes((Join-Path $repo 'src\WinBox.BoxHost\logo.png'), (Png $logoJob))
    }
    finally { $sheet.Dispose() }
    "AppIcon.ico: $($entries.Count) sizes ($(($entries | ForEach-Object { $_.Size }) -join ', ') px); logo.png: $($logoJob.Size) px"
}
finally {
    # Edge's helper processes may still hold the profile for a moment; a leftover in %TEMP% does no harm.
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
