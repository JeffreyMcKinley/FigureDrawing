<#
.SYNOPSIS
  Bumps the app version in version.props — the single source of truth for versionName/versionCode.

.DESCRIPTION
  version.props holds four numbers (major.minor.patch + build); Directory.Build.props derives
  android:versionName and android:versionCode from them. This script is the only supported way to
  change them, because the bump rules are not obvious:

    -Major   1.4.2+3 -> 2.0.0+0   minor, patch and build reset
    -Minor   1.4.2+3 -> 1.5.0+0   patch and build reset
    -Patch   1.4.2+3 -> 1.4.3+0   build resets
    -Build   1.4.2+3 -> 1.4.2+4   same code, new APK (repackaging, new signing key, etc.)
    -Set     an explicit M.m.p or M.m.p.b

  Resetting the build number on every semantic bump is what keeps versionCode monotonic: the code
  packs the numbers as major*1000000 + minor*10000 + patch*100 + build, so a build number carried
  across a patch bump would still increase, but a build number of 100+ would overflow into the
  patch field. minor/patch/build are therefore capped at 99 (also asserted by VersionTests).

.EXAMPLE
  pwsh scripts\bump-version.ps1 -Patch
  pwsh scripts\bump-version.ps1 -Set 2.0.0
  pwsh scripts\bump-version.ps1 -Build -WhatIf
#>
[CmdletBinding(SupportsShouldProcess, DefaultParameterSetName = "Part")]
param(
    [Parameter(ParameterSetName = "Part")][switch]$Major,
    [Parameter(ParameterSetName = "Part")][switch]$Minor,
    [Parameter(ParameterSetName = "Part")][switch]$Patch,
    [Parameter(ParameterSetName = "Part")][switch]$Build,
    [Parameter(ParameterSetName = "Set", Mandatory)][string]$Set
)

$ErrorActionPreference = "Stop"
. "$PSScriptRoot\version-lib.ps1"

$repoRoot = Split-Path -Parent $PSScriptRoot

function Die($msg) { Write-Host $msg -ForegroundColor Red; exit 1 }

$current = Get-FdVersion -RepoRoot $repoRoot
$propsPath = $current.Path
$text = [System.IO.File]::ReadAllText($propsPath)

$v = [ordered]@{
    FdVersionMajor = $current.Major
    FdVersionMinor = $current.Minor
    FdVersionPatch = $current.Patch
    FdBuildNumber  = $current.Build
}
$before = "$($v.FdVersionMajor).$($v.FdVersionMinor).$($v.FdVersionPatch)+$($v.FdBuildNumber)"

if ($PSCmdlet.ParameterSetName -eq "Set") {
    $m = [regex]::Match($Set, '^(?<a>\d+)\.(?<b>\d+)\.(?<c>\d+)(\.(?<d>\d+))?$')
    if (-not $m.Success) { Die "-Set expects M.m.p or M.m.p.b, got '$Set'." }
    $v.FdVersionMajor = [int]$m.Groups["a"].Value
    $v.FdVersionMinor = [int]$m.Groups["b"].Value
    $v.FdVersionPatch = [int]$m.Groups["c"].Value
    $v.FdBuildNumber  = if ($m.Groups["d"].Success) { [int]$m.Groups["d"].Value } else { 0 }
}
elseif ($Major) {
    $v.FdVersionMajor++; $v.FdVersionMinor = 0; $v.FdVersionPatch = 0; $v.FdBuildNumber = 0
}
elseif ($Minor) {
    $v.FdVersionMinor++; $v.FdVersionPatch = 0; $v.FdBuildNumber = 0
}
elseif ($Patch) {
    $v.FdVersionPatch++; $v.FdBuildNumber = 0
}
elseif ($Build) {
    $v.FdBuildNumber++
}
else {
    Die "Pick one of -Major / -Minor / -Patch / -Build / -Set <M.m.p>."
}

foreach ($name in @("FdVersionMinor", "FdVersionPatch", "FdBuildNumber")) {
    if ($v[$name] -gt 99) { Die "$name would become $($v[$name]); 0-99 only (see version.props)." }
}
if ($v.FdVersionMajor -gt 2147) { Die "Major $($v.FdVersionMajor) overflows the int32 versionCode." }

foreach ($name in $v.Keys) {
    $text = [regex]::Replace($text, "<$name>\s*\d+\s*</$name>", "<$name>$($v[$name])</$name>")
}

$after = "$($v.FdVersionMajor).$($v.FdVersionMinor).$($v.FdVersionPatch)+$($v.FdBuildNumber)"
$code  = $v.FdVersionMajor * 1000000 + $v.FdVersionMinor * 10000 + $v.FdVersionPatch * 100 + $v.FdBuildNumber

if ($PSCmdlet.ShouldProcess($propsPath, "bump $before -> $after")) {
    # UTF-8 without a BOM, on both PowerShell editions: Set-Content -Encoding utf8 adds a BOM under
    # 5.1 and not under 7, which would show up as a whole-file diff depending on who ran the bump.
    [System.IO.File]::WriteAllText($propsPath, $text, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "$before -> $after  (versionCode $code)" -ForegroundColor Green
}
else {
    Write-Host "Would bump $before -> $after  (versionCode $code)" -ForegroundColor Yellow
}
