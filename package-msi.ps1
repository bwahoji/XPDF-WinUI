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

  [string]$CertificateSubject = 'CN=Bwahoji',

  [switch]$TrustCertificate,

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
$releaseRoot = Join-Path $repoRoot "artifacts/winui/package/$packageBaseName`_$version`_$Platform"
$payloadRoot = Join-Path $repoRoot "artifacts/winui/msi/$Platform/$Configuration"
$payloadDir = Join-Path $payloadRoot 'publish'
$intermediateDir = Join-Path $payloadRoot 'wix-obj'
$signingRoot = Join-Path $repoRoot "artifacts/winui/signing/$packageBaseName`_$version`_$Platform"
$msiPath = Join-Path $releaseRoot "$packageBaseName`_$version`_$Platform.msi"
$pfxPath = Join-Path $signingRoot "$packageBaseName`_Test.pfx"
$cerPath = Join-Path $releaseRoot "$packageBaseName`_Test.cer"
$passwordPath = Join-Path $signingRoot 'certificate-password.txt'
$productWxs = Join-Path $scriptRoot 'msi/Product.wxs'
$licenseRtf = Join-Path $scriptRoot 'msi/License.rtf'
$iconPath = Join-Path $repoRoot 'xpdf-qt/xpdf-icon.ico'
$projectPath = Join-Path $scriptRoot 'XpdfReader.WinUI/XpdfReader.WinUI.csproj'
$buildScript = Join-Path $scriptRoot 'build.ps1'
$nativeOutputDir = Join-Path $repoRoot "artifacts/winui/$Platform/$Configuration/native"
$runtimeFolder = if ($Platform -eq 'x64') { 'win-x64' } else { 'win-arm64' }
$buildOutputDir = Join-Path $scriptRoot "XpdfReader.WinUI/bin/$Platform/$Configuration/net8.0-windows10.0.19041.0/$runtimeFolder"
$adapterToolsRoot = Join-Path $scriptRoot '.tools'
$sourceToolsRoot = Join-Path $repoRoot '.tools'
$bundledDotnet = if (Test-Path -LiteralPath (Join-Path $adapterToolsRoot 'dotnet/dotnet.exe') -PathType Leaf) {
  Join-Path $adapterToolsRoot 'dotnet/dotnet.exe'
} else {
  Join-Path $sourceToolsRoot 'dotnet/dotnet.exe'
}
$bundledNugetPackages = if (Test-Path -LiteralPath (Join-Path $adapterToolsRoot 'nuget') -PathType Container) {
  Join-Path $adapterToolsRoot 'nuget'
} else {
  Join-Path $sourceToolsRoot 'nuget'
}
$dotnetHome = if (Test-Path -LiteralPath $adapterToolsRoot -PathType Container) {
  Join-Path $adapterToolsRoot 'dotnet-home'
} else {
  Join-Path $sourceToolsRoot 'dotnet-home'
}
$wixRoot = if (Test-Path -LiteralPath $adapterToolsRoot -PathType Container) {
  Join-Path $adapterToolsRoot 'wix'
} else {
  Join-Path $sourceToolsRoot 'wix'
}
$wixExe = Join-Path $wixRoot 'wix.exe'
$windowsKitsBin = 'C:\Program Files (x86)\Windows Kits\10\bin'

function New-DeterministicGuid {
  param(
    [Parameter(Mandatory = $true)]
    [string]$Value
  )

  $sha = [System.Security.Cryptography.SHA256]::Create()
  try {
    $hash = $sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($Value))
  } finally {
    $sha.Dispose()
  }

  $bytes = New-Object byte[] 16
  [Array]::Copy($hash, $bytes, 16)
  $bytes[6] = [byte](($bytes[6] -band 0x0F) -bor 0x50)
  $bytes[8] = [byte](($bytes[8] -band 0x3F) -bor 0x80)
  return ([Guid]::new($bytes)).ToString('B').ToUpperInvariant()
}

function Remove-DirectoryWithin {
  param(
    [Parameter(Mandatory = $true)]
    [string]$Path,

    [Parameter(Mandatory = $true)]
    [string]$Root
  )

  if (-not (Test-Path -LiteralPath $Path)) {
    return
  }

  $resolvedPath = (Resolve-Path -LiteralPath $Path).Path
  $resolvedRoot = (Resolve-Path -LiteralPath $Root).Path.TrimEnd('\') + '\'
  if (-not $resolvedPath.StartsWith($resolvedRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to remove a directory outside $Root"
  }
  Remove-Item -LiteralPath $resolvedPath -Recurse -Force
}

function Get-CodeSigningCertificate {
  param(
    [Parameter(Mandatory = $true)]
    [string]$Subject,

    [Parameter(Mandatory = $true)]
    [string]$PfxPath,

    [Parameter(Mandatory = $true)]
    [string]$PasswordPath,

    [string]$Password
  )

  $effectivePassword = $Password
  if ([string]::IsNullOrWhiteSpace($effectivePassword) -and (Test-Path -LiteralPath $PasswordPath -PathType Leaf)) {
    $effectivePassword = (Get-Content -LiteralPath $PasswordPath -Raw).Trim()
  }

  if (Test-Path -LiteralPath $PfxPath -PathType Leaf) {
    if ([string]::IsNullOrWhiteSpace($effectivePassword)) {
      throw "A signing PFX exists at $PfxPath, but no certificate password was supplied or found at $PasswordPath."
    }

    $existing = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
      $PfxPath,
      $effectivePassword,
      [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
    try {
      if ($existing.Subject -ne $Subject) {
        throw "The existing signing certificate subject '$($existing.Subject)' does not match '$Subject'."
      }
      if (-not $existing.HasPrivateKey) {
        throw "The existing signing certificate does not have a private key."
      }
      if ($existing.NotAfter -le (Get-Date).AddDays(30)) {
        throw "The existing signing certificate expires too soon: $($existing.NotAfter)."
      }
    return @{
      Certificate = $existing
      Password = $effectivePassword
      Reused = $true
      PfxPath = $PfxPath
    }
    } catch {
      $existing.Dispose()
      throw
    }
  }

  if ([string]::IsNullOrWhiteSpace($effectivePassword)) {
    $passwordBytes = New-Object byte[] 24
    $random = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
      $random.GetBytes($passwordBytes)
    } finally {
      $random.Dispose()
    }
    $effectivePassword = [Convert]::ToBase64String($passwordBytes)
  }

  $rsa = [System.Security.Cryptography.RSA]::Create(3072)
  try {
    $distinguishedName = [System.Security.Cryptography.X509Certificates.X500DistinguishedName]::new($Subject)
    $request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
      $distinguishedName,
      $rsa,
      [System.Security.Cryptography.HashAlgorithmName]::SHA256,
      [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)

    $basicConstraints = [System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new(
      $false, $false, 0, $false)
    $keyUsage = [System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
      [System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $true)
    $enhancedUsages = [System.Security.Cryptography.OidCollection]::new()
    $null = $enhancedUsages.Add([System.Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3'))
    $enhancedKeyUsage = [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new(
      $enhancedUsages, $true)
    $subjectKeyIdentifier = [System.Security.Cryptography.X509Certificates.X509SubjectKeyIdentifierExtension]::new(
      $request.PublicKey, $false)

    $request.CertificateExtensions.Add($basicConstraints)
    $request.CertificateExtensions.Add($keyUsage)
    $request.CertificateExtensions.Add($enhancedKeyUsage)
    $request.CertificateExtensions.Add($subjectKeyIdentifier)

    $certificate = $request.CreateSelfSigned((Get-Date).AddDays(-1), (Get-Date).AddYears(5))
    $pfxBytes = $certificate.Export(
      [System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx,
      $effectivePassword)
    [System.IO.File]::WriteAllBytes($PfxPath, $pfxBytes)
    return @{
      Certificate = $certificate
      Password = $effectivePassword
      Reused = $false
      PfxPath = $PfxPath
    }
  } finally {
    $rsa.Dispose()
  }
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

if (Test-Path -LiteralPath $bundledDotnet -PathType Leaf) {
  $dotnetExe = $bundledDotnet
} else {
  $dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
  if (-not $dotnetCommand) {
    throw 'The .NET SDK was not found.'
  }
  $dotnetExe = $dotnetCommand.Source
}

if (-not $env:DOTNET_CLI_HOME) {
  $env:DOTNET_CLI_HOME = $dotnetHome
}
if (-not $env:NUGET_PACKAGES -and (Test-Path -LiteralPath $bundledNugetPackages -PathType Container)) {
  $env:NUGET_PACKAGES = $bundledNugetPackages
}

if (-not (Test-Path -LiteralPath $wixExe -PathType Leaf)) {
  New-Item -ItemType Directory -Force -Path $wixRoot | Out-Null
  & $dotnetExe tool install --tool-path $wixRoot wix --version $wixVersion
  if ($LASTEXITCODE -ne 0) {
    throw 'The WiX command-line tool could not be installed.'
  }
}

$wixExtensions = (& $wixExe extension list 2>$null) -join "`n"
if ($wixExtensions -notmatch 'WixToolset\.UI\.wixext') {
  & $wixExe extension add "WixToolset.UI.wixext/$wixVersion"
  if ($LASTEXITCODE -ne 0) {
    throw 'The WiX UI extension could not be installed.'
  }
}

$signTool = Get-ChildItem -LiteralPath $windowsKitsBin -Recurse -File -Filter 'signtool.exe' |
  Where-Object { $_.FullName -match '\\x64\\' } |
  Sort-Object FullName -Descending |
  Select-Object -First 1
if (-not $signTool) {
  throw 'SignTool.exe was not found in the Windows SDK.'
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

Remove-DirectoryWithin -Path $releaseRoot -Root (Join-Path $repoRoot 'artifacts/winui/package')
Remove-DirectoryWithin -Path $payloadDir -Root (Join-Path $repoRoot 'artifacts/winui/msi')
Remove-DirectoryWithin -Path $intermediateDir -Root (Join-Path $repoRoot 'artifacts/winui/msi')
New-Item -ItemType Directory -Force -Path $releaseRoot | Out-Null
New-Item -ItemType Directory -Force -Path $payloadDir | Out-Null
New-Item -ItemType Directory -Force -Path $intermediateDir | Out-Null
New-Item -ItemType Directory -Force -Path $signingRoot | Out-Null

$publishArguments = @(
  'publish',
  $projectPath,
  '-c', $Configuration,
  "-p:Platform=$Platform",
  "-p:NativeBridgeDir=$nativeOutputDir",
  "-p:PublishDir=$payloadDir\",
  '--no-restore'
)
& $dotnetExe @publishArguments
if ($LASTEXITCODE -ne 0) {
  throw 'Application publish failed.'
}

Get-ChildItem -LiteralPath $payloadDir -Recurse -File -Filter '*.pdb' | Remove-Item -Force

$appPri = Join-Path $buildOutputDir 'XpdfReader.WinUI.pri'
if (-not (Test-Path -LiteralPath $appPri -PathType Leaf)) {
  throw "Application resource index was not found: $appPri"
}
Copy-Item -LiteralPath $appPri -Destination (Join-Path $payloadDir 'XpdfReader.WinUI.pri') -Force
Copy-Item -LiteralPath $appPri -Destination (Join-Path $payloadDir 'resources.pri') -Force

if (-not (Test-Path -LiteralPath (Join-Path $payloadDir 'XpdfReader.WinUI.exe') -PathType Leaf)) {
  throw 'The published application executable was not found.'
}

$productCode = New-DeterministicGuid "$upgradeCode|$version"
$wixArguments = @(
  'build',
  $productWxs,
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

$externalCertificatePath = $null
if (-not [string]::IsNullOrWhiteSpace($CertificatePath)) {
  if (-not (Test-Path -LiteralPath $CertificatePath -PathType Leaf)) {
    throw "The code-signing certificate PFX was not found: $CertificatePath"
  }
  $externalCertificatePath = (Resolve-Path -LiteralPath $CertificatePath).Path
  try {
    $externalCertificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
      $externalCertificatePath,
      $CertificatePassword,
      [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
  } catch {
    throw "The code-signing certificate PFX could not be loaded: $($_.Exception.Message)"
  }
  if (-not $externalCertificate.HasPrivateKey) {
    $externalCertificate.Dispose()
    throw 'The supplied code-signing certificate does not have a private key.'
  }
  $externalCertificateNotAfter = $externalCertificate.NotAfter
  if ($externalCertificateNotAfter -le (Get-Date)) {
    $externalCertificate.Dispose()
    throw "The supplied code-signing certificate has expired: $externalCertificateNotAfter."
  }
  $signing = @{
    Certificate = $externalCertificate
    Password = $CertificatePassword
    Reused = $true
    PfxPath = $externalCertificatePath
  }
  if ($TrustCertificate) {
    $externalCertificate.Dispose()
    throw 'TrustCertificate is only for the automatically generated test certificate.'
  }
} else {
  $signing = Get-CodeSigningCertificate `
    -Subject $CertificateSubject `
    -PfxPath $pfxPath `
    -PasswordPath $passwordPath `
    -Password $CertificatePassword
}
$certificate = $signing.Certificate
$effectivePassword = $signing.Password
$signingPfxPath = $signing.PfxPath
try {
  $cerBytes = $certificate.Export(
    [System.Security.Cryptography.X509Certificates.X509ContentType]::Cert)
  [System.IO.File]::WriteAllBytes($cerPath, $cerBytes)
} finally {
  if (-not $signing.Reused) {
    $certificate.Dispose()
  }
}

if (-not $externalCertificatePath) {
  Set-Content -LiteralPath $passwordPath -Value $effectivePassword -NoNewline -Encoding ascii
}

$signArguments = @(
  'sign',
  '/fd', 'SHA256',
  '/f', $signingPfxPath,
  '/p', $effectivePassword,
  '/d', $packageBaseName
)
if (-not $NoTimestamp) {
  $signArguments += @('/td', 'SHA256', '/tr', 'http://timestamp.digicert.com')
}
$signArguments += $msiPath

& $signTool.FullName @signArguments
if ($LASTEXITCODE -ne 0 -and -not $NoTimestamp) {
  Write-Warning 'Timestamping failed; signing the MSI without a timestamp.'
  $fallbackArguments = @(
    'sign',
    '/fd', 'SHA256',
    '/f', $signingPfxPath,
    '/p', $effectivePassword,
    '/d', $packageBaseName,
    $msiPath
  )
  & $signTool.FullName @fallbackArguments
}
if ($LASTEXITCODE -ne 0) {
  throw 'SignTool failed.'
}

if ($TrustCertificate) {
  Import-Certificate -FilePath $cerPath -CertStoreLocation 'Cert:\CurrentUser\TrustedPeople' | Out-Null
  & certutil.exe -user -addstore -f Root $cerPath | Out-Null
  & $signTool.FullName verify /pa /v $msiPath
  if ($LASTEXITCODE -ne 0) {
    throw 'The signed MSI could not be verified after trusting the certificate.'
  }
}

$installCertificateScript = Join-Path $releaseRoot 'Install-Certificate.ps1'
$installMsiScript = Join-Path $releaseRoot 'Install-XPDF-WinUI.ps1'
$releaseReadme = Join-Path $releaseRoot 'README.txt'

if (-not $externalCertificatePath) {
  @"
`$ErrorActionPreference = 'Stop'
`$certificatePath = Join-Path `$PSScriptRoot '$($cerPath | Split-Path -Leaf)'
Import-Certificate -FilePath `$certificatePath -CertStoreLocation 'Cert:\CurrentUser\TrustedPeople' | Out-Null
& certutil.exe -user -addstore -f Root `$certificatePath | Out-Null
Write-Host 'The XPDF-WinUI test certificate is trusted for the current user.'
"@ | Set-Content -LiteralPath $installCertificateScript -Encoding utf8
}

@"
`$ErrorActionPreference = 'Stop'
`$msiPath = Join-Path `$PSScriptRoot '$($msiPath | Split-Path -Leaf)'
Start-Process -FilePath msiexec.exe -ArgumentList @('/i', "`"`$msiPath`"") -Wait
Write-Host 'XPDF-WinUI installation finished.'
"@ | Set-Content -LiteralPath $installMsiScript -Encoding utf8

if ($externalCertificatePath) {
  $certificateTrustStep = 'No test certificate installation is required.'
  $certificateNotice = "Signed with: $($certificate.Subject)"
  $certificateKeyNotice = 'The code-signing private key is not included in this distribution folder.'
} else {
  $certificateTrustStep = '1. Run Install-Certificate.ps1 once for the current user if this is the self-signed test package.'
  $certificateNotice = @"
The included certificate is a self-signed test certificate. For public
distribution, replace it with a trusted code-signing certificate whose
subject exactly matches $CertificateSubject, then sign the MSI again.
"@
  $certificateKeyNotice = 'The PFX private key and its password are stored outside this distribution folder under artifacts\winui\signing and must not be shared with end users.'
}

@"
XPDF-WinUI $version

Publisher: $publisher
Description: $description
License: GPL v3
Architecture: x64
Installer: Windows Installer (MSI), per-machine

Installation:
$certificateTrustStep
Double-click the MSI, or run Install-XPDF-WinUI.ps1.

The MSI installs XPDF-WinUI under Program Files, adds a Start menu shortcut,
and registers XPDF-WinUI as an available PDF application. Windows may still
ask the user to choose a default PDF application; the installer does not
overwrite an existing per-user default association.

$certificateNotice

$certificateKeyNotice
"@ | Set-Content -LiteralPath $releaseReadme -Encoding utf8

$hash = Get-FileHash -LiteralPath $msiPath -Algorithm SHA256
$signature = Get-AuthenticodeSignature -LiteralPath $msiPath
Write-Host ''
Write-Host "MSI: $msiPath"
Write-Host "ProductCode: $productCode"
Write-Host "Certificate: $cerPath"
Write-Host "Signing files (do not distribute): $signingRoot"
Write-Host "Signature status: $($signature.Status)"
Write-Host "Signature subject: $($signature.SignerCertificate.Subject)"
Write-Host "SHA-256: $($hash.Hash)"
