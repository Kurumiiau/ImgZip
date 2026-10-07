# Build the ImgZip installer (MSI).
# Requires: .NET 8 SDK + WiX Toolset v5 (`dotnet tool install --global wix --version 5.0.2`).
#
# Usage: powershell -ExecutionPolicy Bypass -File build-installer.ps1
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here            # repository root
$payload = Join-Path $root 'release\payload'
$msi = Join-Path $root 'release\ImgZip-Setup-1.2.0.msi'

$wix = Join-Path $env:USERPROFILE '.dotnet\tools\wix.exe'
if (-not (Test-Path $wix)) { $wix = 'wix' }

# 1/2 复用根目录 build.ps1 完成 publish（其中包含把 ffmpeg/ffprobe 放进 payload 的步骤）
Write-Output '--- 1/2 publish application into release\payload ---'
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'build.ps1')
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

if (-not (Test-Path (Join-Path $payload 'ffmpeg.exe'))) {
  Write-Output '警告：payload 中没有 ffmpeg.exe，安装包将不含视频压缩能力'
}

Write-Output '--- 2/2 build MSI ---'
Push-Location $here
try {
  & $wix build ImgZip.wxs -arch x64 -ext WixToolset.UI.wixext/5.0.2 -o $msi
  if ($LASTEXITCODE -ne 0) { throw 'wix build failed' }
} finally { Pop-Location }

Write-Output ('done -> ' + $msi + '  (' + [math]::Round((Get-Item $msi).Length / 1MB, 1) + ' MB)')
