<#
.SYNOPSIS
  Builds an installable FigureDrawing APK and copies it to artifacts\.

.DESCRIPTION
  Sideload flow (no emulator, no MSBuild -t:Run):

    1. `dotnet publish -c Release -p:AndroidPackageFormat=apk` -> a signed, self-contained APK
       (Release publish defaults to .aab, which `adb install` cannot take, hence the explicit format)
    2. copies the signed APK to artifacts\FigureDrawing-<config>.apk
    3. optionally `adb install -r` it onto an attached phone (-Install)

  Signing: with no -KeyStore the APK is signed with the local Android debug key. That installs on a
  normal phone (USB / "install unknown apps") but is NOT suitable for Play Store upload. Pass
  -KeyStore/-KeyAlias/-KeyPass for a real release key.

  Debug config is supported (-Configuration Debug) but forces EmbedAssembliesIntoApk=true; a Debug
  APK built without it uses Fast Deployment and crashes at launch with "No assemblies found" when
  installed via adb.

  Android SDK + JDK 17 paths come from Directory.Build.props; they are passed explicitly here too so
  the script works from a shell with a different JAVA_HOME.

.EXAMPLE
  pwsh scripts\build-apk.ps1
  pwsh scripts\build-apk.ps1 -Install                       # build, then adb install to the phone
  pwsh scripts\build-apk.ps1 -Configuration Debug -Install
  pwsh scripts\build-apk.ps1 -KeyStore C:\keys\fd.keystore -KeyAlias fd -KeyPass hunter2
#>
[CmdletBinding()]
param(
    [ValidateSet("Release", "Debug")]
    [string]$Configuration = "Release",
    [string]$Jdk = $env:JavaSdkDirectory,
    [string]$Sdk = $env:AndroidSdkDirectory,
    [string]$OutDir = "",
    [switch]$Install,
    [string]$Target = "",
    [string]$KeyStore = "",
    [string]$KeyAlias = "",
    [string]$KeyPass = ""
)

# adb/dotnet write progress to stderr, which PowerShell surfaces as NativeCommandError; 'Stop' would
# abort on harmless output. Native steps are checked explicitly via $LASTEXITCODE.
$ErrorActionPreference = "Continue"
$repoRoot = Split-Path -Parent $PSScriptRoot

function Die($msg) { Write-Host $msg -ForegroundColor Red; exit 1 }

if (-not $Jdk) { $Jdk = "C:\Program Files\Microsoft\jdk-17.0.19.10-hotspot" }
if (-not $Sdk) { $Sdk = Join-Path $env:LOCALAPPDATA "Android\Sdk" }
if (-not $OutDir) { $OutDir = Join-Path $repoRoot "artifacts" }

$env:ANDROID_HOME     = $Sdk
$env:ANDROID_SDK_ROOT = $Sdk
$env:JAVA_HOME        = $Jdk

$project = Join-Path $repoRoot "FigureDrawing.csproj"
$tfm     = "net9.0-android"

# 1. Publish ---------------------------------------------------------------------------------------
$publishArgs = @(
    $project,
    "-c", $Configuration,
    "-f", $tfm,
    "--nologo",
    "-p:AndroidPackageFormat=apk",
    "-p:JavaSdkDirectory=$Jdk",
    "-p:AndroidSdkDirectory=$Sdk"
)
if ($Configuration -eq "Debug") { $publishArgs += "-p:EmbedAssembliesIntoApk=true" }
if ($KeyStore) {
    if (-not (Test-Path $KeyStore)) { Die "Keystore not found: $KeyStore" }
    if (-not $KeyAlias) { Die "-KeyAlias is required with -KeyStore." }
    $publishArgs += @(
        "-p:AndroidKeyStore=true",
        "-p:AndroidSigningKeyStore=$KeyStore",
        "-p:AndroidSigningKeyAlias=$KeyAlias",
        "-p:AndroidSigningKeyPass=$KeyPass",
        "-p:AndroidSigningStorePass=$KeyPass"
    )
}

Write-Host "Publishing $Configuration APK..." -ForegroundColor Cyan
& dotnet publish @publishArgs
if ($LASTEXITCODE -ne 0) { Die "Publish failed." }

# 2. Locate + copy ---------------------------------------------------------------------------------
$publishDir = Join-Path $repoRoot "bin\$Configuration\$tfm\publish"
# Prefer the *-Signed.apk; only that one is zipaligned + signed and installable.
$apk = Get-ChildItem -Path $publishDir -Filter "*-Signed.apk" -ErrorAction SilentlyContinue |
       Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $apk) {
    $apk = Get-ChildItem -Path $publishDir -Filter "*.apk" -ErrorAction SilentlyContinue |
           Sort-Object LastWriteTime -Descending | Select-Object -First 1
}
if (-not $apk) { Die "No APK found in $publishDir" }

if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }
$dest = Join-Path $OutDir "FigureDrawing-$($Configuration.ToLower()).apk"
Copy-Item $apk.FullName $dest -Force

$sizeMb = [math]::Round((Get-Item $dest).Length / 1MB, 1)
Write-Host "APK: $dest ($sizeMb MB)" -ForegroundColor Green

# 3. Optional install ------------------------------------------------------------------------------
if ($Install) {
    $adb = Join-Path $Sdk "platform-tools\adb.exe"
    if (-not (Test-Path $adb)) { Die "adb not found at $adb" }
    $adbArgs = @()
    if ($Target) { $adbArgs += @("-s", $Target) }
    $adbArgs += @("install", "-r", $dest)

    Write-Host "Installing to device..." -ForegroundColor Cyan
    & $adb @adbArgs
    if ($LASTEXITCODE -ne 0) { Die "adb install failed." }
    Write-Host "Installed." -ForegroundColor Green
}
