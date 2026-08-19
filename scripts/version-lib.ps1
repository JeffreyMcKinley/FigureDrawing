<#
.SYNOPSIS
  Shared reader for version.props. Dot-source it: . "$PSScriptRoot\version-lib.ps1"

.DESCRIPTION
  Keeps the versionCode packing in one place for the scripts. It is the same formula
  Directory.Build.props computes in MSBuild — VersionTests asserts the two agree, so if you change
  one, change the other.
#>

function Get-FdVersion {
    [CmdletBinding()]
    param([string]$RepoRoot = (Split-Path -Parent $PSScriptRoot))

    $propsPath = Join-Path $RepoRoot "version.props"
    if (-not (Test-Path $propsPath)) { throw "version.props not found at $propsPath" }
    # .NET's reader, not Get-Content: Windows PowerShell 5.1 reads a BOM-less UTF-8 file as ANSI and
    # the bump script would write that back mangled.
    $text = [System.IO.File]::ReadAllText($propsPath)

    $part = {
        param($name)
        $m = [regex]::Match($text, "<$name>\s*(?<v>\d+)\s*</$name>")
        if (-not $m.Success) { throw "version.props has no <$name> element." }
        [int]$m.Groups["v"].Value
    }

    $major = & $part "FdVersionMajor"
    $minor = & $part "FdVersionMinor"
    $patch = & $part "FdVersionPatch"
    $build = & $part "FdBuildNumber"

    $semantic = "$major.$minor.$patch"
    [pscustomobject]@{
        Major    = $major
        Minor    = $minor
        Patch    = $patch
        Build    = $build
        Semantic = $semantic
        # Matches ApplicationDisplayVersion / android:versionName.
        Full     = if ($build -eq 0) { $semantic } else { "$semantic.$build" }
        # Matches ApplicationVersion / android:versionCode.
        Code     = $major * 1000000 + $minor * 10000 + $patch * 100 + $build
        Path     = $propsPath
    }
}

<#
.SYNOPSIS
  The next build number, or an error if the field is full.

.DESCRIPTION
  build-apk.ps1 consumes one build number per APK so two builds of the same semantic version never
  share a versionCode. At 99 the field is full: the next semantic bump (which resets it to 0) is the
  only way forward, because 100 would carry into the patch slot of the packed code.
#>
function Get-FdNextBuildNumber {
    [CmdletBinding()]
    param([string]$RepoRoot = (Split-Path -Parent $PSScriptRoot))

    $current = (Get-FdVersion -RepoRoot $RepoRoot).Build
    if ($current -ge 99) {
        throw "FdBuildNumber is at $current; 0-99 only. Bump the version (scripts\bump-version.ps1 -Patch) to reset it."
    }
    $current + 1
}

<#
.SYNOPSIS
  Writes FdBuildNumber back into version.props, leaving the semantic numbers alone.

.DESCRIPTION
  The one supported writer for that field: it range-checks (0-99, same limit VersionTests asserts)
  and writes UTF-8 without a BOM, because Set-Content -Encoding utf8 adds one under Windows
  PowerShell 5.1 and not under 7 — a whole-file diff depending on who ran the build.
#>
function Set-FdBuildNumber {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][int]$Build,
        [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
    )

    if ($Build -lt 0 -or $Build -gt 99) {
        throw "FdBuildNumber must be 0-99, got $Build (see version.props)."
    }

    $propsPath = (Get-FdVersion -RepoRoot $RepoRoot).Path
    $text = [System.IO.File]::ReadAllText($propsPath)
    $text = [regex]::Replace($text, "<FdBuildNumber>\s*\d+\s*</FdBuildNumber>", "<FdBuildNumber>$Build</FdBuildNumber>")
    [System.IO.File]::WriteAllText($propsPath, $text, (New-Object System.Text.UTF8Encoding($false)))
}
