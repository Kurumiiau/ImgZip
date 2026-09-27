# Build the ImgZip installer (MSI).
# Requires: .NET 8 SDK + WiX Toolset v5 (`dotnet tool install --global wix --version 5.0.2`).
#
# Usage: powershell -ExecutionPolicy Bypass -File build-installer.ps1
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here            # repository root
$payload = Join-Path $root 'release\payload'
$msi = Join-Path $root 'release\ImgZip-Setup-1.0.0.msi'

$sdk = 'E:\tools\dotnet8\dotnet.exe'
if (-not (Test-Path $sdk)) { $sdk = 'dotnet' }
$wix = Join-Path $env:USERPROFILE '.dotnet\tools\wix.exe'
if (-not (Test-Path $wix)) { $wix = 'wix' }

Write-Output '--- 1/2 publish application into release\payload ---'
if (Test-Path $payload) { Remove-Item -Recurse -Force $payload }
& $sdk publish (Join-Path $root 'src\ImgZip\ImgZip.csproj') -c Release -o $payload --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

Write-Output '--- 2/2 build MSI ---'
Push-Location $here
try {
  & $wix build ImgZip.wxs -arch x64 -ext WixToolset.UI.wixext/5.0.2 -o $msi
  if ($LASTEXITCODE -ne 0) { throw 'wix build failed' }
} finally { Pop-Location }

Write-Output ('done -> ' + $msi + '  (' + [math]::Round((Get-Item $msi).Length / 1KB, 0) + ' KB)')
