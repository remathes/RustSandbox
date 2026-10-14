<#
Builds the Windows installer for Rust Sandbox.

  1. Publishes a self-contained win-x64 build to publish\win-x64 (no .NET needed on the target PC).
  2. Compiles installer\RustSandbox.iss with Inno Setup 6.

The setup file lands in installer\output. Run from anywhere:

  powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 [-Version 1.2.0]

Needs the .NET 8 SDK and Inno Setup 6 (winget install JRSoftware.InnoSetup).
#>
param([string]$Version)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\RustSandbox\RustSandbox.csproj'
$publish = Join-Path $root 'publish\win-x64'
# Everything this script prints also goes to installer\output\build-log.txt, for troubleshooting.
$null = New-Item -ItemType Directory -Force (Join-Path $PSScriptRoot 'output')
Start-Transcript -Path (Join-Path $PSScriptRoot 'output\build-log.txt') -Force | Out-Null
$script = Join-Path $PSScriptRoot 'RustSandbox.iss'

if (-not $Version) {
    $Version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if (-not $Version) { $Version = '1.0.0' }
}

$iscc = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source),
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) {
    throw 'Inno Setup 6 is not installed. Install it with "winget install JRSoftware.InnoSetup" or from https://jrsoftware.org/isdl.php, then run this again.'
}
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'The .NET 8 SDK is not installed (https://dotnet.microsoft.com/download/dotnet/8.0).'
}

# The app icon is drawn by make-icon.ps1; it's only regenerated when it's missing.
$icon = Join-Path $root 'src\RustSandbox\app.ico'
if (-not (Test-Path $icon)) {
    & (Join-Path $PSScriptRoot 'make-icon.ps1') -Ico $icon
}

Write-Host "Publishing Rust Sandbox $Version for win-x64..." -ForegroundColor Cyan
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
# The Razor source generator sometimes crashes with a NullReferenceException inside the shared compiler
# server (more often while Visual Studio has the project open), which then shows up as "The type 'App'
# could not be found". Compile in-process instead, and try again if it still fails.
dotnet build-server shutdown | Out-Null
for ($attempt = 1; ; $attempt++) {
    dotnet publish $project -c Release -r win-x64 --self-contained true -o $publish -p:Version=$Version -p:DebugType=none -p:UseSharedCompilation=false -p:UseRazorBuildServer=false
    if ($LASTEXITCODE -eq 0) { break }
    if ($attempt -ge 3) { throw "dotnet publish failed (exit code $LASTEXITCODE) after $attempt tries." }
    Write-Host "dotnet publish failed (exit code $LASTEXITCODE); trying again ($($attempt + 1) of 3)..." -ForegroundColor Yellow
    dotnet build-server shutdown | Out-Null
    if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
}

# The same inputs always give the same setup file, so compiling twice and comparing catches a build
# that got damaged while compressing (it would fail on install with "The source file is corrupted").
$setup = Join-Path $PSScriptRoot "output\RustSandbox-Setup-$Version.exe"
$check = Join-Path ([IO.Path]::GetTempPath()) 'sandbox-installer-check'
# A crash inside Inno Setup is retried the same way as a mismatch.
for ($attempt = 1; ; $attempt++) {
    Write-Host "Compiling the installer with $iscc..." -ForegroundColor Cyan
    & $iscc /Q "/DAppVersion=$Version" $script | Out-Host
    $failed = $LASTEXITCODE -ne 0
    if (-not $failed) {
        Write-Host 'Compiling it again to check the build...' -ForegroundColor Cyan
        & $iscc /Q "/DAppVersion=$Version" "/O$check" $script | Out-Host
        $failed = $LASTEXITCODE -ne 0
    }
    if ($failed) {
        if (Test-Path -LiteralPath $check) { Remove-Item -LiteralPath $check -Recurse -Force }
        if ($attempt -ge 3) {
            if (Test-Path -LiteralPath $setup) { Remove-Item -LiteralPath $setup -Force }
            throw "Inno Setup failed (exit code $LASTEXITCODE) on 3 attempts in a row."
        }
        Write-Warning "Inno Setup failed (exit code $LASTEXITCODE, attempt $attempt of 3); building again."
        continue
    }
    $first = (Get-FileHash $setup).Hash
    $second = (Get-FileHash (Join-Path $check (Split-Path $setup -Leaf))).Hash
    Write-Host "First build:  $first ($((Get-Item $setup).Length) bytes)"
    Write-Host "Second build: $second ($((Get-Item (Join-Path $check (Split-Path $setup -Leaf))).Length) bytes)"
    if ($first -ne $second) {
        # Keep both copies of a mismatched build so the difference can be inspected. Don't install these.
        Copy-Item -LiteralPath $setup -Destination (Join-Path $PSScriptRoot "output\mismatch-$attempt-a.exe.bad") -Force
        Copy-Item -LiteralPath (Join-Path $check (Split-Path $setup -Leaf)) -Destination (Join-Path $PSScriptRoot "output\mismatch-$attempt-b.exe.bad") -Force
    }
    Remove-Item -LiteralPath $check -Recurse -Force
    if ($first -eq $second) { break }
    if ($attempt -ge 3) {
        Remove-Item -LiteralPath $setup -Force
        throw 'The two builds kept coming out different, so the setup file can''t be trusted and was deleted. With the zip compressor this shouldn''t happen; if it does, the PC itself is corrupting data (try turning off any overclock or XMP memory profile in the BIOS).'
    }
    Write-Warning "The two builds didn't match (attempt $attempt of 3); building again."
}
Write-Host "Done: $setup" -ForegroundColor Green
Stop-Transcript | Out-Null
