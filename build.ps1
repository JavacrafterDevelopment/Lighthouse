# Builds the portable Lighthouse folder.
#
#   powershell -ExecutionPolicy Bypass -File build.ps1
#
# Produces dist\Lighthouse\ containing Lighthouse.exe and every dependency it
# needs — the .NET runtime is published self-contained, so the folder can be
# copied to any 64-bit Windows machine and run with nothing installed.

param(
    [string]$Configuration = 'Release',
    [switch]$SkipIcon,
    [switch]$Zip
)

$ErrorActionPreference = 'Stop'
$root    = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root 'src\Lighthouse\Lighthouse.csproj'
$dist    = Join-Path $root 'dist\Lighthouse'

Write-Host ''
Write-Host '  Lighthouse - portable build' -ForegroundColor DarkYellow
Write-Host '  ---------------------------' -ForegroundColor DarkGray

if (-not $SkipIcon) {
    Write-Host '  [1/3] Drawing application icon'
    & (Join-Path $root 'tools\make-icon.ps1') | Out-Null
}

Write-Host "  [2/3] Publishing ($Configuration, win-x64, self-contained)"

# A running copy holds its own exe open, and it may well be elevated and so not
# ours to kill. Say so plainly rather than failing later with a copy error.
$exe = Join-Path $dist 'Lighthouse.exe'
if (Test-Path $exe) {
    try {
        $handle = [IO.File]::Open($exe, 'Open', 'ReadWrite', 'None')
        $handle.Close()
    }
    catch {
        throw "Lighthouse is still running from $dist. Right-click its tray icon and choose Exit, then build again."
    }
}

if (Test-Path $dist) {
    # Leave data\ alone: it holds the WebView2 profile and is not build output.
    Get-ChildItem $dist -Force | Where-Object { $_.Name -ne 'data' } | Remove-Item -Recurse -Force
}

# ReadyToRun trades a larger folder for a noticeably faster cold start, which
# matters for something you summon with a hotkey.
& dotnet publish $project `
    --configuration $Configuration `
    --runtime win-x64 `
    --self-contained true `
    --output $dist `
    -p:PublishReadyToRun=true `
    -p:DebugType=none `
    -p:GenerateDocumentationFile=false `
    --nologo `
    --verbosity quiet

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

Write-Host '  [3/3] Tidying'

# Publishing drops the WebView2 reference assemblies and XML docs; neither is
# needed at runtime.
Get-ChildItem $dist -Filter '*.xml' -ErrorAction SilentlyContinue | Remove-Item -Force
$wpf = Join-Path $dist 'Microsoft.Web.WebView2.Wpf.dll'
if (Test-Path $wpf) { Remove-Item $wpf -Force }

# WebView2 keeps its browser profile beside the exe so the whole thing stays portable.
New-Item -ItemType Directory -Force -Path (Join-Path $dist 'data') | Out-Null

$readme = Join-Path $root 'README.md'
if (Test-Path $readme) { Copy-Item $readme (Join-Path $dist 'README.md') -Force }

# data\ is the runtime browser profile, not part of the shipped app.
$shipped = Get-ChildItem $dist -Recurse -File |
           Where-Object { $_.FullName -notlike (Join-Path $dist 'data\*') }
$size = ($shipped | Measure-Object -Property Length -Sum).Sum
Write-Host ''
Write-Host ('  Done - {0}' -f $dist) -ForegroundColor Green
Write-Host ('  {0} files, {1:N0} MB' -f $shipped.Count, ($size / 1MB))
Write-Host ''

if ($Zip) {
    $zipPath = Join-Path $root 'dist\Lighthouse-portable-win-x64.zip'
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path $dist -DestinationPath $zipPath
    Write-Host ('  Archive: {0} ({1:N0} MB)' -f $zipPath, ((Get-Item $zipPath).Length / 1MB))
    Write-Host ''
}
