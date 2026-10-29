param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$AppProj = Join-Path $RepoRoot "src\PinDock\PinDock.csproj"
$SetupProj = Join-Path $RepoRoot "installer\PinDock.Setup\PinDock.Setup.csproj"

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$csprojXml = Get-Content $AppProj
    $Version = ($csprojXml.Project.PropertyGroup.Version | Select-Object -First 1)
    if ([string]::IsNullOrWhiteSpace($Version)) { $Version = "0.1.0" }
}

Write-Host "==> [1/4] Publishing PinDock v$Version ($Configuration / $Runtime)..." -ForegroundColor Cyan
$PublishDir = Join-Path $RepoRoot "artifacts\publish"
if (Test-Path $PublishDir) { Remove-Item -Recurse -Force $PublishDir }

dotnet publish $AppProj `
    -c $Configuration `
    -r $Runtime `
    --self-contained false `
    -p:Version=$Version `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -o $PublishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for PinDock." }

Write-Host "==> [2/4] Packing payload archive..." -ForegroundColor Cyan
$PayloadZip = Join-Path $RepoRoot "installer\PinDock.Setup\payload.zip"
if (Test-Path $PayloadZip) { Remove-Item -Force $PayloadZip }
Compress-Archive -Path (Join-Path $PublishDir "*") -DestinationPath $PayloadZip -CompressionLevel Optimal

Write-Host "==> [3/4] Compiling installer package PinDock-Setup-$Version.exe..." -ForegroundColor Cyan
$DistDir = Join-Path $RepoRoot "dist"
New-Item -ItemType Directory -Force -Path $DistDir | Out-Null
$SetupStaging = Join-Path $RepoRoot "artifacts\setup-publish"
if (Test-Path $SetupStaging) { Remove-Item -Recurse -Force $SetupStaging }

dotnet publish $SetupProj `
    -c $Configuration `
    -r $Runtime `
    --self-contained false `
    -p:PublishSingleFile=true `
    -p:Version=$Version `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -o $SetupStaging
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for PinDock.Setup." }

$SourceSetupExe = Join-Path $SetupStaging "PinDock-Setup.exe"
$TargetInstaller = Join-Path $DistDir "PinDock-Setup-$Version.exe"
Copy-Item -Force $SourceSetupExe $TargetInstaller

# Clean up temporary payload.zip
if (Test-Path $PayloadZip) { Remove-Item -Force $PayloadZip }

$FileInfo = Get-Item $TargetInstaller
$SizeMB = [Math]::Round($FileInfo.Length / 1MB, 2)
Write-Host "==> [4/4] Installer built successfully: $TargetInstaller ($SizeMB MB)" -ForegroundColor Green
