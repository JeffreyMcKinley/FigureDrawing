<#
.SYNOPSIS
  Runs the FigureDrawing Appium end-to-end UI tests (FigureDrawing.UITests).

.DESCRIPTION
  These tests need infrastructure that a normal `nx test` deliberately skips. This script wires it
  all up, runs the suite with RUN_APPIUM=1, and tears the server down afterwards:

    1. installs Appium + the uiautomator2 driver if missing (global npm)
    2. boots the FigureDrawing_Pixel emulator if no device is attached
    3. builds + installs the signed APK
    4. starts an Appium server, waits for it, runs the tests, stops the server

  Prereqs (see repo memory): JDK 17 + Android SDK. Override paths with the params below or the
  matching environment variables.
#>
[CmdletBinding()]
param(
    [string]$Jdk = $env:JavaSdkDirectory,
    [string]$Sdk = $env:AndroidSdkDirectory,
    [string]$Avd = "FigureDrawing_Pixel",
    [string]$AppiumUrl = "http://127.0.0.1:4723"
)

# NOTE: deliberately NOT 'Stop'. appium/adb/emulator log progress to stderr, which PowerShell
# wraps as NativeCommandError; with 'Stop' that aborts the script on harmless output. Critical
# native steps are checked explicitly via $LASTEXITCODE / Die below instead.
$ErrorActionPreference = "Continue"
$repoRoot = Split-Path -Parent $PSScriptRoot

function Die($msg) { Write-Host $msg -ForegroundColor Red; exit 1 }

if (-not $Jdk) { $Jdk = "C:\Program Files\Microsoft\jdk-17.0.19.10-hotspot" }
if (-not $Sdk) { $Sdk = Join-Path $env:LOCALAPPDATA "Android\Sdk" }

$adb      = Join-Path $Sdk "platform-tools\adb.exe"
$emulator = Join-Path $Sdk "emulator\emulator.exe"

# The Appium server + uiautomator2 driver locate adb/tools via these; JAVA_HOME must be JDK 17.
$env:ANDROID_HOME     = $Sdk
$env:ANDROID_SDK_ROOT = $Sdk
$env:JAVA_HOME        = $Jdk

function Wait-For([scriptblock]$Condition, [int]$TimeoutSec, [string]$What) {
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    while ((Get-Date) -lt $deadline) {
        if (& $Condition) { return }
        Start-Sleep -Seconds 2
    }
    Die "Timed out waiting for: $What"
}

# 1. Appium + driver ------------------------------------------------------------------------------
if (-not (Get-Command appium -ErrorAction SilentlyContinue)) {
    Write-Host "Installing Appium (global)..." -ForegroundColor Cyan
    npm install -g appium
    if ($LASTEXITCODE -ne 0) { Die "npm install -g appium failed." }
}
$drivers = (& appium driver list --installed 2>&1 | Out-String)
if ($drivers -notmatch "uiautomator2") {
    Write-Host "Installing uiautomator2 driver..." -ForegroundColor Cyan
    & appium driver install uiautomator2 2>&1 | Write-Host
}

# 2. Emulator -------------------------------------------------------------------------------------
$attached = (& $adb devices) | Select-String "device$"
if (-not $attached) {
    Write-Host "Booting emulator $Avd..." -ForegroundColor Cyan
    Start-Process -FilePath $emulator -ArgumentList "-avd", $Avd, "-no-snapshot-save"
    & $adb wait-for-device
    Wait-For { (& $adb shell getprop sys.boot_completed 2>$null).Trim() -eq "1" } 180 "emulator boot"
}
Write-Host "Device ready." -ForegroundColor Green

# 3. Build + install APK --------------------------------------------------------------------------
Write-Host "Building signed APK..." -ForegroundColor Cyan
# EmbedAssembliesIntoApk=true disables Fast Deployment so the .NET assemblies live INSIDE the APK.
# Without it, a plain `adb install` of a Debug APK launches to a native abort ("No assemblies found
# in .../.__override__") because Fast Deployment expects the assemblies pushed separately (as
# `-t:Run` does). A self-contained APK is required for Appium to install + launch it standalone.
& dotnet build (Join-Path $repoRoot "FigureDrawing.csproj") -c Debug --nologo `
    -p:JavaSdkDirectory=$Jdk -p:AndroidSdkDirectory=$Sdk -p:EmbedAssembliesIntoApk=true
if ($LASTEXITCODE -ne 0) { Die "Android build failed." }
$apk = Join-Path $repoRoot "bin\Debug\net9.0-android\com.companyname.FigureDrawing-Signed.apk"
if (-not (Test-Path $apk)) { Die "APK not found at $apk" }
& $adb install -r $apk 2>&1 | Write-Host
if ($LASTEXITCODE -ne 0) { Die "adb install failed." }

# 4. Appium server + tests ------------------------------------------------------------------------
Write-Host "Starting Appium server..." -ForegroundColor Cyan
# `appium` on PATH is a PowerShell shim (appium.ps1) which Start-Process cannot launch; use the
# .cmd shim next to it. Server logs go to appium-server.log for debugging a failed session.
$appiumCmd = (Get-Command appium.cmd -ErrorAction SilentlyContinue).Source
if (-not $appiumCmd) { $appiumCmd = Join-Path $env:APPDATA "npm\appium.cmd" }
if (-not (Test-Path $appiumCmd)) { Die "appium.cmd not found at $appiumCmd" }
$log = Join-Path $repoRoot "appium-server.log"
$appium = Start-Process -FilePath $appiumCmd -ArgumentList "--relaxed-security" -PassThru `
    -WindowStyle Hidden -RedirectStandardOutput $log -RedirectStandardError "$log.err"
try {
    $port = ([Uri]$AppiumUrl).Port
    Wait-For { Test-NetConnection 127.0.0.1 -Port $port -InformationLevel Quiet } 60 "Appium server"

    $env:RUN_APPIUM = "1"
    $env:APPIUM_URL = $AppiumUrl
    dotnet test (Join-Path $repoRoot "FigureDrawing.UITests\FigureDrawing.UITests.csproj") --nologo
    exit $LASTEXITCODE
}
finally {
    Write-Host "Stopping Appium server..." -ForegroundColor Cyan
    if ($appium -and -not $appium.HasExited) { Stop-Process -Id $appium.Id -Force }
    Remove-Item Env:\RUN_APPIUM, Env:\APPIUM_URL -ErrorAction SilentlyContinue
}
