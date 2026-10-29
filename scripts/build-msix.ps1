param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Version = "",
    [string]$IdentityName = "flylink.PinDock",
    [string]$PublisherId = "CN=flylink",
    [string]$PublisherDisplayName = "flylink"
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$AppProj = Join-Path $RepoRoot "src\PinDock\PinDock.csproj"
$ManifestTemplate = Join-Path $RepoRoot "installer\msix\Package.appxmanifest"
$AssetsDir = Join-Path $RepoRoot "installer\msix\Assets"

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$csprojXml = Get-Content $AppProj
    $Version = ($csprojXml.Project.PropertyGroup.Version | Select-Object -First 1)
    if ([string]::IsNullOrWhiteSpace($Version)) { $Version = "0.1.5" }
}

$FourPartVersion = if ($Version.Split('.').Count -eq 3) { "$Version.0" } else { $Version }

Write-Host "==> [1/3] Publishing PinDock v$Version ($Configuration / $Runtime) for MSIX..." -ForegroundColor Cyan
$MsixStage = Join-Path $RepoRoot "artifacts\msix-stage"
if (Test-Path $MsixStage) { Remove-Item -Recurse -Force $MsixStage }
New-Item -ItemType Directory -Force -Path $MsixStage | Out-Null

dotnet publish $AppProj `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:Version=$Version `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -o $MsixStage
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for MSIX stage." }

Write-Host "==> [2/3] Staging AppxManifest.xml and Store Visual Assets..." -ForegroundColor Cyan
$StageAssets = Join-Path $MsixStage "Assets"
New-Item -ItemType Directory -Force -Path $StageAssets | Out-Null
Copy-Item -Path (Join-Path $AssetsDir "*.png") -Destination $StageAssets -Force

$manifestContent = Get-Content $ManifestTemplate -Raw -Encoding UTF8
$manifestContent = $manifestContent.Replace("__IDENTITY_NAME__", $IdentityName)
$manifestContent = $manifestContent.Replace("__PUBLISHER_ID__", $PublisherId)
$manifestContent = $manifestContent.Replace("__PUBLISHER_DISPLAY_NAME__", $PublisherDisplayName)
$manifestContent = $manifestContent.Replace("__VERSION__", $FourPartVersion)
Set-Content -Path (Join-Path $MsixStage "AppxManifest.xml") -Value $manifestContent -Encoding UTF8

Write-Host "==> [3/3] Packing MSIX with makeappx.exe..." -ForegroundColor Cyan
$MakeAppx = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin\*\x64\makeappx.exe" -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending |
    Select-Object -First 1

if (-not $MakeAppx) {
    throw "makeappx.exe not found in Windows Kits\10\bin. Please install Windows 10/11 SDK."
}

$DistDir = Join-Path $RepoRoot "dist"
New-Item -ItemType Directory -Force -Path $DistDir | Out-Null
$OutMsix = Join-Path $DistDir "PinDock-$Version-x64.msix"
if (Test-Path $OutMsix) { Remove-Item -Force $OutMsix }

& $MakeAppx.FullName pack /d $MsixStage /p $OutMsix /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makeappx.exe pack failed." }

$SizeMB = [Math]::Round((Get-Item $OutMsix).Length / 1MB, 2)
Write-Host "==> MSIX built successfully: $OutMsix ($SizeMB MB)" -ForegroundColor Green
