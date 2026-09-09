[CmdletBinding()]
param(
  [ValidateSet('Release', 'Debug')]
  [string]$Configuration = 'Release',

  [ValidateSet('x64')]
  [string]$Platform = 'x64',

  [switch]$SkipBuild,

  [switch]$SkipTests,

  [string]$CertificatePath,

  [string]$CertificatePassword,

  [switch]$NoTimestamp
)

$ErrorActionPreference = 'Stop'

$scriptRoot = Split-Path -Parent $PSCommandPath
$repoRoot = Split-Path -Parent $scriptRoot
$version = '0.0.0.1'
$packageBaseName = 'XPDF-WinUI'
$publisher = 'Bwahoji'
$description = 'A WinUI version of Xpdf Reader'
$wixVersion = '5.0.2'
$upgradeCode = '{D5B1E3A7-5B7C-4F2A-9B6E-4C8D1A2E3F40}'
# Change this GUID when publishing a new product version.
$productCode = '{836C464A-7CF0-345A-92A1-33B26A145CF8}'
$releaseRoot = Join-Path $repoRoot "artifacts/winui/package/$packageBaseName`_$version`_$Platform"
$payloadRoot = Join-Path $repoRoot "artifacts/winui/msi/$Platform/$Configuration"
$payloadDir = Join-Path $payloadRoot 'publish'
$intermediateDir = Join-Path $payloadRoot 'wix-obj'
$msiPath = Join-Path $releaseRoot "$packageBaseName`_$version`_$Platform.msi"
$productWxs = Join-Path $scriptRoot 'msi/Product.wxs'
$licenseRtf = Join-Path $scriptRoot 'msi/License.rtf'
$iconPath = Join-Path $repoRoot 'xpdf-qt/xpdf-icon.ico'
$projectPath = Join-Path $scriptRoot 'XpdfReader.WinUI/XpdfReader.WinUI.csproj'
$buildScript = Join-Path $scriptRoot 'build.ps1'
$nativeOutputDir = Join-Path $repoRoot "artifacts/winui/$Platform/$Configuration/native"

function Reset-Directory {
  param(
    [Parameter(Mandatory = $true)]
    [string]$Path
  )

  if (Test-Path -LiteralPath $Path) {
    Remove-Item -LiteralPath $Path -Recurse -Force
  }
  New-Item -ItemType Directory -Force -Path $Path | Out-Null
}

if (-not (Test-Path -LiteralPath $productWxs -PathType Leaf)) {
  throw "WiX source was not found: $productWxs"
}
if (-not (Test-Path -LiteralPath $licenseRtf -PathType Leaf)) {
  throw "License RTF was not found: $licenseRtf"
}
if (-not (Test-Path -LiteralPath $iconPath -PathType Leaf)) {
  throw "Application icon was not found: $iconPath"
}

$dotnetExe = (Get-Command dotnet -ErrorAction Stop).Source
$wixCommand = Get-Command wix -ErrorAction SilentlyContinue
if ($wixCommand) {
  $wixExe = $wixCommand.Source
} else {
  $wixCandidates = @(
    (Join-Path $scriptRoot '.tools/wix/wix.exe'),
    (Join-Path $repoRoot '.tools/wix/wix.exe')
  )
  $wixExe = $wixCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}
if (-not $wixExe) {
  throw "WiX 5 was not found. Install it with: dotnet tool install --global wix --version $wixVersion"
}

Push-Location $repoRoot
try {
  $wixExtensions = (& $wixExe extension list 2>$null) -join "`n"
  if ($wixExtensions -notmatch 'WixToolset\.UI\.wixext') {
    & $wixExe extension add "WixToolset.UI.wixext/$wixVersion"
    if ($LASTEXITCODE -ne 0) {
      throw 'The WiX UI extension could not be installed.'
    }
  }
} finally {
  Pop-Location
}

if (-not $SkipBuild) {
  if ($SkipTests) {
    & $buildScript -Configuration $Configuration -Platform $Platform -SkipTests
  } else {
    & $buildScript -Configuration $Configuration -Platform $Platform
  }
}

if (-not (Test-Path -LiteralPath $nativeOutputDir -PathType Container)) {
  throw "Native bridge output was not found: $nativeOutputDir"
}

Reset-Directory $releaseRoot
Reset-Directory $payloadDir
Reset-Directory $intermediateDir

& $dotnetExe publish $projectPath `
  -c $Configuration `
  "-p:Platform=$Platform" `
  "-p:NativeBridgeDir=$nativeOutputDir" `
  "-p:PublishDir=$payloadDir\" `
  --no-restore
if ($LASTEXITCODE -ne 0) {
  throw 'Application publish failed.'
}

Get-ChildItem -LiteralPath $payloadDir -Recurse -File -Filter '*.pdb' | Remove-Item -Force

$binRoot = Join-Path $scriptRoot 'XpdfReader.WinUI/bin'
if (-not (Test-Path -LiteralPath $binRoot -PathType Container)) {
  throw "WinUI build output was not found: $binRoot"
}
$appPri = Get-ChildItem -Path $binRoot `
  -Recurse -File -Filter 'XpdfReader.WinUI.pri' |
  Sort-Object LastWriteTime -Descending |
  Select-Object -First 1
if (-not $appPri) {
  throw 'XpdfReader.WinUI.pri was not found after publishing.'
}
Copy-Item -LiteralPath $appPri.FullName -Destination (Join-Path $payloadDir 'XpdfReader.WinUI.pri') -Force
Copy-Item -LiteralPath $appPri.FullName -Destination (Join-Path $payloadDir 'resources.pri') -Force

$wixArguments = @(
  'build', $productWxs,
  '-arch', 'x64',
  '-ext', 'WixToolset.UI.wixext',
  '-d', "ProductName=$packageBaseName",
  '-d', "Manufacturer=$publisher",
  '-d', "Description=$description",
  '-d', "ProductVersion=$version",
  '-d', "ProductCode=$productCode",
  '-d', "UpgradeCode=$upgradeCode",
  '-d', "PublishDir=$payloadDir",
  '-d', "IconPath=$iconPath",
  '-d', "LicenseRtf=$licenseRtf",
  '-intermediatefolder', $intermediateDir,
  '-pdbtype', 'none',
  '-o', $msiPath
)
& $wixExe @wixArguments
if ($LASTEXITCODE -ne 0) {
  throw 'WiX MSI build failed.'
}

if ($CertificatePath) {
  if (-not (Test-Path -LiteralPath $CertificatePath -PathType Leaf)) {
    throw "Code-signing certificate was not found: $CertificatePath"
  }

  $signTool = Get-Command signtool.exe -ErrorAction SilentlyContinue
  if ($signTool) {
    $signToolPath = $signTool.Source
  } else {
    $signTool = Get-ChildItem 'C:\Program Files (x86)\Windows Kits\10\bin' `
      -Recurse -File -Filter 'signtool.exe' |
      Where-Object { $_.FullName -match '\\x64\\' } |
      Sort-Object FullName -Descending |
      Select-Object -First 1
    if ($signTool) {
      $signToolPath = $signTool.FullName
    }
  }
  if (-not $signToolPath) {
    throw 'SignTool.exe was not found. Install the Windows SDK or add signtool.exe to PATH.'
  }

  $signArguments = @(
    'sign', '/fd', 'SHA256',
    '/f', $CertificatePath,
    '/d', $packageBaseName
  )
  if ($CertificatePassword) {
    $signArguments += @('/p', $CertificatePassword)
  }
  if (-not $NoTimestamp) {
    $signArguments += @('/td', 'SHA256', '/tr', 'http://timestamp.digicert.com')
  }
  $signArguments += $msiPath

  & $signToolPath @signArguments
  if ($LASTEXITCODE -ne 0 -and -not $NoTimestamp) {
    Write-Warning 'Timestamping failed; signing without a timestamp.'
    $signArguments = @(
      'sign', '/fd', 'SHA256',
      '/f', $CertificatePath,
      '/d', $packageBaseName
    )
    if ($CertificatePassword) {
      $signArguments += @('/p', $CertificatePassword)
    }
    $signArguments += $msiPath
    & $signToolPath @signArguments
  }
  if ($LASTEXITCODE -ne 0) {
    throw 'SignTool failed.'
  }
}

$hash = Get-FileHash -LiteralPath $msiPath -Algorithm SHA256
$signature = Get-AuthenticodeSignature -LiteralPath $msiPath
Write-Host ''
Write-Host "MSI: $msiPath"
Write-Host "ProductCode: $productCode"
Write-Host "Signature status: $($signature.Status)"
Write-Host "SHA-256: $($hash.Hash)"
