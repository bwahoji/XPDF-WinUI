[CmdletBinding()]
param(
  [ValidateSet('Debug', 'Release')]
  [string]$Configuration = 'Release',

  [ValidateSet('x64', 'ARM64')]
  [string]$Platform = 'x64',

  [string]$FreetypeDir,

  [string]$Generator = 'Ninja',

  [string]$CompilerBinDir,

  [string]$RuntimeDependencyDir,

  [string[]]$RuntimeDependencyName = @(),

  [switch]$SkipTests,

  [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'

$scriptRoot = Split-Path -Parent $PSCommandPath
$repoRoot = Split-Path -Parent $scriptRoot
$artifactRoot = Join-Path $repoRoot "artifacts/winui/$Platform/$Configuration"
$cmakeBuildDir = Join-Path $artifactRoot 'cmake'
$nativeOutputDir = Join-Path $artifactRoot 'native'
$projectPath = Join-Path $scriptRoot 'XpdfReader.WinUI/XpdfReader.WinUI.csproj'

if (-not (Test-Path -LiteralPath (Join-Path $repoRoot 'CMakeLists.txt') -PathType Leaf)) {
  throw "This script must be placed at <Xpdf source>/xpdf-winui. Run integrate.ps1 first."
}
if ($FreetypeDir -and -not (Test-Path -LiteralPath $FreetypeDir -PathType Container)) {
  throw "FreetypeDir does not exist: $FreetypeDir"
}
if ($RuntimeDependencyName.Count -gt 0 -and -not $RuntimeDependencyDir) {
  throw 'RuntimeDependencyName requires RuntimeDependencyDir.'
}
if ($RuntimeDependencyDir -and -not (Test-Path -LiteralPath $RuntimeDependencyDir -PathType Container)) {
  throw "RuntimeDependencyDir does not exist: $RuntimeDependencyDir"
}
if ($CompilerBinDir -and -not (Test-Path -LiteralPath $CompilerBinDir -PathType Container)) {
  throw "CompilerBinDir does not exist: $CompilerBinDir"
}

$cmakeExe = (Get-Command cmake -ErrorAction Stop).Source
$ctestExe = (Get-Command ctest -ErrorAction Stop).Source
$dotnetExe = (Get-Command dotnet -ErrorAction Stop).Source
if ($Generator -match 'Ninja' -and -not (Get-Command ninja -ErrorAction SilentlyContinue)) {
  throw 'Ninja was not found. Add ninja.exe to PATH or pass a Visual Studio generator.'
}

if ($CompilerBinDir) {
  $gccExe = Join-Path $CompilerBinDir 'gcc.exe'
  $gxxExe = Join-Path $CompilerBinDir 'c++.exe'
  if (-not (Test-Path -LiteralPath $gccExe -PathType Leaf) -or
      -not (Test-Path -LiteralPath $gxxExe -PathType Leaf)) {
    throw "CompilerBinDir must contain gcc.exe and c++.exe: $CompilerBinDir"
  }
  $env:Path = "$CompilerBinDir;$env:Path"
}

$cmakeArguments = @(
  '-S', $repoRoot,
  '-B', $cmakeBuildDir,
  '-G', $Generator,
  '-DXPDF_BUILD_WINUI=ON',
  '-DBUILD_TESTING=ON',
  "-DCMAKE_BUILD_TYPE=$Configuration",
  '-DCMAKE_DISABLE_FIND_PACKAGE_Qt5Widgets=ON',
  '-DCMAKE_DISABLE_FIND_PACKAGE_Qt6Widgets=ON'
)
if ($FreetypeDir) {
  $cmakeArguments += "-DFREETYPE_DIR=$FreetypeDir"
}
if ($CompilerBinDir) {
  $cmakeArguments += @("-DCMAKE_C_COMPILER=$gccExe", "-DCMAKE_CXX_COMPILER=$gxxExe")
}
if ($Generator -match 'Visual Studio') {
  $cmakeArguments += @('-A', $Platform)
}

& $cmakeExe @cmakeArguments
if ($LASTEXITCODE -ne 0) {
  throw 'CMake configuration failed.'
}

& $cmakeExe --build $cmakeBuildDir --config $Configuration --target xpdf_winui_native
if ($LASTEXITCODE -ne 0) {
  throw 'Native bridge build failed.'
}

if (-not $SkipTests) {
  & $cmakeExe --build $cmakeBuildDir --config $Configuration --target xpdf_winui_native_tests
  if ($LASTEXITCODE -ne 0) {
    throw 'Native bridge test build failed.'
  }
  & $ctestExe --test-dir $cmakeBuildDir --build-config $Configuration --output-on-failure
  if ($LASTEXITCODE -ne 0) {
    throw 'Native bridge tests failed.'
  }
}

New-Item -ItemType Directory -Force -Path $nativeOutputDir | Out-Null
$nativeBridge = Get-ChildItem -Path $cmakeBuildDir -Recurse -File -Filter 'xpdf_winui_native.dll' |
  Select-Object -First 1
if (-not $nativeBridge) {
  throw "xpdf_winui_native.dll was not found under $cmakeBuildDir"
}
Copy-Item -LiteralPath $nativeBridge.FullName -Destination $nativeOutputDir -Force

foreach ($dependencyName in $RuntimeDependencyName) {
  $dependencyPath = Join-Path $RuntimeDependencyDir $dependencyName
  if (-not (Test-Path -LiteralPath $dependencyPath -PathType Leaf)) {
    throw "Runtime dependency was not found: $dependencyPath"
  }
  Copy-Item -LiteralPath $dependencyPath -Destination $nativeOutputDir -Force
}

$dotnetArguments = @(
  'build', $projectPath,
  '-c', $Configuration,
  "-p:Platform=$Platform",
  "-p:NativeBridgeDir=$nativeOutputDir"
)
if ($NoRestore) {
  $dotnetArguments += '--no-restore'
}
& $dotnetExe @dotnetArguments
if ($LASTEXITCODE -ne 0) {
  throw 'WinUI build failed.'
}

Write-Host "WinUI output: $scriptRoot/XpdfReader.WinUI/bin/$Platform/$Configuration"
