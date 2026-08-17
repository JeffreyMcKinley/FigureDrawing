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
