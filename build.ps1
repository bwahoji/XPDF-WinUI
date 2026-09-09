[CmdletBinding()]
param(
  [ValidateSet('Debug', 'Release')]
  [string]$Configuration = 'Release',

  [ValidateSet('x64', 'ARM64')]
  [string]$Platform = 'x64',

  # Defaults to the development dependency staged with this checkout.
  [string]$FreetypeDir,

  # Defaults to Ninja when using the FreeType dependency staged with this checkout.
  [string]$Generator,

  # Directory containing gcc.exe and c++.exe for a Ninja build.
  [string]$CompilerBinDir,

  # Directory containing optional toolchain runtime DLLs.
  [string]$RuntimeDependencyDir,

  # Names of runtime DLLs to stage from RuntimeDependencyDir.
  [string[]]$RuntimeDependencyName = @(),

  [switch]$SkipTests,

  # Build from restored packages without contacting configured NuGet sources.
  [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'

$scriptRoot = Split-Path -Parent $PSCommandPath
$repoRoot = Split-Path -Parent $scriptRoot
$artifactRoot = Join-Path $repoRoot "artifacts/winui/$Platform/$Configuration"
$cmakeBuildDir = Join-Path $artifactRoot 'cmake'
$nativeOutputDir = Join-Path $artifactRoot 'native'
$projectPath = Join-Path $scriptRoot 'XpdfReader.WinUI/XpdfReader.WinUI.csproj'

function Resolve-BundledPath {
  param(
    [Parameter(Mandatory = $true)]
    [string]$RelativePath
  )

  $adapterPath = Join-Path $scriptRoot ".tools/$RelativePath"
  if (Test-Path -LiteralPath $adapterPath) {
    return $adapterPath
  }
  return Join-Path $repoRoot ".tools/$RelativePath"
}

$bundledFreetypeDir = Resolve-BundledPath 'freetype'
$bundledCmake = Resolve-BundledPath 'cmake/cmake/data/bin/cmake.exe'
$bundledDotnet = Resolve-BundledPath 'dotnet/dotnet.exe'
$bundledNugetPackages = Resolve-BundledPath 'nuget'
$dotnetHome = if (Test-Path -LiteralPath (Join-Path $scriptRoot '.tools') -PathType Container) {
  Join-Path $scriptRoot '.tools/dotnet-home'
} else {
  Join-Path $repoRoot '.tools/dotnet-home'
}

if ([string]::IsNullOrWhiteSpace($FreetypeDir)) {
  $FreetypeDir = $bundledFreetypeDir
}

if (-not (Test-Path -LiteralPath $FreetypeDir -PathType Container)) {
  throw "FreetypeDir does not exist: $FreetypeDir. Install FreeType there or pass -FreetypeDir <path-to-freetype-prefix>."
}
if ($RuntimeDependencyDir -and -not (Test-Path -LiteralPath $RuntimeDependencyDir -PathType Container)) {
  throw "RuntimeDependencyDir does not exist: $RuntimeDependencyDir"
}
if ($CompilerBinDir -and -not (Test-Path -LiteralPath $CompilerBinDir -PathType Container)) {
  throw "CompilerBinDir does not exist: $CompilerBinDir"
}

$usingBundledFreetype = (Resolve-Path -LiteralPath $FreetypeDir).Path -eq
  (Resolve-Path -LiteralPath $bundledFreetypeDir).Path
if (-not $CompilerBinDir -and $usingBundledFreetype) {
  $defaultCompilerBinDir = 'C:\msys64\ucrt64\bin'
  if (Test-Path -LiteralPath (Join-Path $defaultCompilerBinDir 'c++.exe') -PathType Leaf) {
    $CompilerBinDir = $defaultCompilerBinDir
  }
}

if (-not $Generator) {
  $Generator = if ($CompilerBinDir) { 'Ninja' } else { 'Visual Studio 17 2022' }
}

$cmakeCommand = Get-Command cmake -ErrorAction SilentlyContinue
if ($cmakeCommand) {
  $cmakeExe = $cmakeCommand.Source
} elseif (Test-Path -LiteralPath $bundledCmake -PathType Leaf) {
  $cmakeExe = $bundledCmake
} else {
  throw 'CMake was not found. Install CMake or add cmake.exe to PATH.'
}
$ctestExe = Join-Path (Split-Path -Parent $cmakeExe) 'ctest.exe'
if (-not (Test-Path -LiteralPath $ctestExe -PathType Leaf)) {
  $ctestCommand = Get-Command ctest -ErrorAction SilentlyContinue
  if (-not $ctestCommand) { throw 'CTest was not found beside CMake or on PATH.' }
  $ctestExe = $ctestCommand.Source
}

if (Test-Path -LiteralPath $bundledDotnet -PathType Leaf) {
  $dotnetExe = $bundledDotnet
} else {
  $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
  if (-not $dotnetCommand) { throw 'The .NET 8 SDK was not found. Install it or restore .tools/dotnet.' }
  $dotnetExe = $dotnetCommand.Source
}
if (-not $env:NUGET_PACKAGES -and (Test-Path -LiteralPath $bundledNugetPackages -PathType Container)) {
  $env:NUGET_PACKAGES = $bundledNugetPackages
}
if (-not $env:DOTNET_CLI_HOME) {
  $env:DOTNET_CLI_HOME = $dotnetHome
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
  "-DFREETYPE_DIR=$FreetypeDir",
  "-DCMAKE_BUILD_TYPE=$Configuration",
  '-DCMAKE_DISABLE_FIND_PACKAGE_Qt5Widgets=ON',
  '-DCMAKE_DISABLE_FIND_PACKAGE_Qt6Widgets=ON'
)
if ($CompilerBinDir) {
  $cmakeArguments += @("-DCMAKE_C_COMPILER=$gccExe", "-DCMAKE_CXX_COMPILER=$gxxExe")
}
if ($Generator -match 'Ninja') {
  $ninjaCommand = Get-Command ninja -ErrorAction SilentlyContinue
  $visualStudioNinja = 'C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe'
  if ($ninjaCommand) {
    $ninjaExe = $ninjaCommand.Source
  } elseif (Test-Path -LiteralPath $visualStudioNinja -PathType Leaf) {
    $ninjaExe = $visualStudioNinja
  } else {
    throw 'Ninja was not found. Install Ninja or add ninja.exe to PATH.'
  }
  $cmakeArguments += "-DCMAKE_MAKE_PROGRAM=$ninjaExe"
} else {
  $cmakeArguments += @('-A', $Platform)
}

& $cmakeExe @cmakeArguments
if ($LASTEXITCODE -ne 0) { throw 'CMake configuration failed.' }

& $cmakeExe --build $cmakeBuildDir --config $Configuration --target xpdf_winui_native
if ($LASTEXITCODE -ne 0) { throw 'Native bridge build failed.' }

if (-not $SkipTests) {
  & $cmakeExe --build $cmakeBuildDir --config $Configuration --target xpdf_winui_native_tests
  if ($LASTEXITCODE -ne 0) { throw 'Native bridge test build failed.' }

  & $ctestExe --test-dir $cmakeBuildDir --build-config $Configuration --output-on-failure
  if ($LASTEXITCODE -ne 0) { throw 'Native bridge tests failed.' }
}

New-Item -ItemType Directory -Force -Path $nativeOutputDir | Out-Null
$nativeBridge = Get-ChildItem -Path $cmakeBuildDir -Recurse -File -Filter 'xpdf_winui_native.dll' |
  Select-Object -First 1
if (-not $nativeBridge) {
  throw "CMake completed but xpdf_winui_native.dll was not found under $cmakeBuildDir"
}
Copy-Item -LiteralPath $nativeBridge.FullName -Destination $nativeOutputDir -Force

foreach ($dependencyName in $RuntimeDependencyName) {
  if (-not $RuntimeDependencyDir) {
    throw 'RuntimeDependencyName requires RuntimeDependencyDir.'
  }
  $dependencyPath = Join-Path $RuntimeDependencyDir $dependencyName
  if (-not (Test-Path -LiteralPath $dependencyPath -PathType Leaf)) {
    throw "Runtime dependency was not found: $dependencyPath"
  }
  Copy-Item -LiteralPath $dependencyPath -Destination $nativeOutputDir -Force
}

if ($usingBundledFreetype -and -not $RuntimeDependencyDir) {
  $RuntimeDependencyDir = $CompilerBinDir
  $RuntimeDependencyName = @('libgcc_s_seh-1.dll', 'libstdc++-6.dll', 'libwinpthread-1.dll')
  foreach ($dependencyName in $RuntimeDependencyName) {
    $dependencyPath = Join-Path $RuntimeDependencyDir $dependencyName
    if (-not (Test-Path -LiteralPath $dependencyPath -PathType Leaf)) {
      throw "Runtime dependency was not found: $dependencyPath"
    }
    Copy-Item -LiteralPath $dependencyPath -Destination $nativeOutputDir -Force
  }
}

$dotnetArguments = @('build', $projectPath, '-c', $Configuration,
  "-p:Platform=$Platform", "-p:NativeBridgeDir=$nativeOutputDir")
if ($NoRestore) {
  $dotnetArguments += '--no-restore'
}
& $dotnetExe @dotnetArguments
if ($LASTEXITCODE -ne 0) { throw 'WinUI build failed.' }

Write-Host "WinUI output: $scriptRoot/XpdfReader.WinUI/bin/$Platform/$Configuration"
