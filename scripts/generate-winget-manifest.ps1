param(
    [string]$Version = ""
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$AppProj = Join-Path $RepoRoot "src\PinDock\PinDock.csproj"

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$csprojXml = Get-Content $AppProj
    $Version = ($csprojXml.Project.PropertyGroup.Version | Select-Object -First 1)
    if ([string]::IsNullOrWhiteSpace($Version)) { $Version = "0.1.5" }
}

$InstallerPath = Join-Path $RepoRoot "dist\PinDock-Setup-$Version.exe"
if (-not (Test-Path $InstallerPath)) {
    throw "Installer not found at $InstallerPath. Run scripts\build-installer.ps1 first."
}

$Sha256 = (Get-FileHash -Path $InstallerPath -Algorithm SHA256).Hash.ToUpperInvariant()
$OutDir = Join-Path $RepoRoot "installer\winget\flylink-code.PinDock\$Version"
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$VersionYaml = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.version.1.6.0.schema.json
PackageIdentifier: flylink-code.PinDock
PackageVersion: $Version
DefaultLocale: zh-CN
ManifestType: version
ManifestVersion: 1.6.0
"@

$InstallerYaml = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.installer.1.6.0.schema.json
PackageIdentifier: flylink-code.PinDock
PackageVersion: $Version
InstallerType: exe
Scope: user
InstallModes:
  - interactive
  - silent
InstallerSwitches:
  Silent: /SILENT
  SilentWithProgress: /SILENT
UpgradeBehavior: install
ReleaseDate: $(Get-Date -Format "yyyy-MM-dd")
Installers:
  - Architecture: x64
    InstallerUrl: https://github.com/flylink-code/PinDock/releases/download/v$Version/PinDock-Setup-$Version.exe
    InstallerSha256: $Sha256
ManifestType: installer
ManifestVersion: 1.6.0
"@

$LocaleZhYaml = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.defaultLocale.1.6.0.schema.json
PackageIdentifier: flylink-code.PinDock
PackageVersion: $Version
PackageLocale: zh-CN
Publisher: flylink-code
PublisherUrl: https://github.com/flylink-code
PackageName: PinDock
PackageUrl: https://github.com/flylink-code/PinDock
License: MIT
LicenseUrl: https://github.com/flylink-code/PinDock/blob/main/LICENSE
PrivacyUrl: https://github.com/flylink-code/PinDock/blob/main/PRIVACY.md
ShortDescription: Windows 独立悬浮启动栏与目录多级预览工具，不替换原生任务栏。
Tags:
  - dock
  - launcher
  - productivity
  - windows
ReleaseNotesUrl: https://github.com/flylink-code/PinDock/releases/tag/v$Version
ManifestType: defaultLocale
ManifestVersion: 1.6.0
"@

$LocaleEnYaml = @"
# yaml-language-server: `$schema=https://aka.ms/winget-manifest.locale.1.6.0.schema.json
PackageIdentifier: flylink-code.PinDock
PackageVersion: $Version
PackageLocale: en-US
Publisher: flylink-code
PublisherUrl: https://github.com/flylink-code
PackageName: PinDock
PackageUrl: https://github.com/flylink-code/PinDock
License: MIT
LicenseUrl: https://github.com/flylink-code/PinDock/blob/main/LICENSE
PrivacyUrl: https://github.com/flylink-code/PinDock/blob/main/PRIVACY.md
ShortDescription: Standalone floating dock, folder hover preview, and quick launcher for Windows.
Tags:
  - dock
  - launcher
  - productivity
  - windows
ReleaseNotesUrl: https://github.com/flylink-code/PinDock/releases/tag/v$Version
ManifestType: locale
ManifestVersion: 1.6.0
"@

Set-Content -Path (Join-Path $OutDir "flylink-code.PinDock.yaml") -Value $VersionYaml -Encoding UTF8
Set-Content -Path (Join-Path $OutDir "flylink-code.PinDock.installer.yaml") -Value $InstallerYaml -Encoding UTF8
Set-Content -Path (Join-Path $OutDir "flylink-code.PinDock.locale.zh-CN.yaml") -Value $LocaleZhYaml -Encoding UTF8
Set-Content -Path (Join-Path $OutDir "flylink-code.PinDock.locale.en-US.yaml") -Value $LocaleEnYaml -Encoding UTF8

Write-Host "==> Generated WinGet manifests in: $OutDir (SHA256: $Sha256)" -ForegroundColor Green
