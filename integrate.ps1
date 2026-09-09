[CmdletBinding()]
param(
  [string]$XpdfSourceDir = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $XpdfSourceDir -PathType Container)) {
  throw "Xpdf source directory was not found: $XpdfSourceDir"
}

$sourceDir = (Resolve-Path -LiteralPath $XpdfSourceDir).Path.TrimEnd('\', '/')
$adapterDir = (Resolve-Path -LiteralPath $PSScriptRoot).Path.TrimEnd('\', '/')
$sourcePrefix = $sourceDir + [System.IO.Path]::DirectorySeparatorChar

if (-not $adapterDir.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase)) {
  throw "This repository must be placed inside the Xpdf source tree. Current adapter path: $adapterDir"
}

$relativeAdapter = $adapterDir.Substring($sourcePrefix.Length).Replace('\', '/')
$cmakePath = Join-Path $sourceDir 'CMakeLists.txt'
if (-not (Test-Path -LiteralPath $cmakePath -PathType Leaf)) {
  throw "CMakeLists.txt was not found in $sourceDir"
}

$content = [System.IO.File]::ReadAllText($cmakePath)
if ($content -match 'XPDF_BUILD_WINUI') {
  Write-Host 'XPDF_BUILD_WINUI is already present in CMakeLists.txt. Nothing to do.'
  exit 0
}

$optionLine = 'option(XPDF_BUILD_WINUI "Build the native bridge for the WinUI 3 reader" OFF)'
$winuiBlock = @"
if (XPDF_BUILD_WINUI)
  include(CTest)
  add_subdirectory($relativeAdapter/native)
endif ()
"@

$updated = $content -replace '(?m)^(project\(xpdf\)\s*)$', "`$1`r`n`r`n$optionLine"
if ($updated -eq $content) {
  throw 'Could not find project(xpdf) in CMakeLists.txt.'
}

$beforeBlock = $updated
$updated = $updated -replace '(?m)^(add_subdirectory\(xpdf-qt\)\s*)$', "`$1`r`n`r`n$winuiBlock"
if ($updated -eq $beforeBlock) {
  throw 'Could not find add_subdirectory(xpdf-qt) in CMakeLists.txt.'
}

[System.IO.File]::WriteAllText(
  $cmakePath,
  $updated,
  [System.Text.UTF8Encoding]::new($false))

Write-Host "Integrated XPDF-WinUI into $cmakePath"
Write-Host "Next: .\xpdf-winui\build.ps1 -Configuration Release -Platform x64"
