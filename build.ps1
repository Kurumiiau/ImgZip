# Build script for ImgZip (image & video compressor).
# Produces the framework-dependent build into release\payload\ — the same folder
# the installer packages (see installer\build-installer.ps1).
# Requires the .NET 8 SDK; the target machine needs the .NET 8 Desktop Runtime.
#
# Usage:  powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root 'src\ImgZip\ImgZip.csproj'
$out = Join-Path $root 'release\payload'

# Prefer portable SDKs used during development, fall back to a system install.
$sdkCandidates = @('E:\tools\dotnet8\dotnet.exe', 'E:\KimiWork\dotnet-sdk\dotnet.exe')
$sdk = $sdkCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $sdk) { $sdk = 'dotnet' }
Write-Output ("using SDK: " + $sdk)

Write-Output '--- publish ---'
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
& $sdk publish $proj -c Release -o $out --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

# ---- 视频：把 ffmpeg / ffprobe 一并放进 payload（安装包会整体打包） ----
$ffmpegCandidates = @(
  (Join-Path $env:LOCALAPPDATA 'ImgZip\ffmpeg'),          # 本仓库约定位置
  'E:\KimiWork\ffmpeg\bin',                                # 开发机位置
  'E:\tools\ffmpeg\bin'
)
$found = $ffmpegCandidates | Where-Object { Test-Path (Join-Path $_ 'ffmpeg.exe') } | Select-Object -First 1
if ($found) {
  Copy-Item (Join-Path $found 'ffmpeg.exe')  $out -Force
  Copy-Item (Join-Path $found 'ffprobe.exe') $out -Force
  Write-Output 'ffmpeg / ffprobe 已随包附带（视频压缩开箱即用）'
} else {
  Write-Output '警告：未找到 ffmpeg，安装包将不含视频压缩能力（程序运行时也会从 PATH 查找）'
}

Write-Output '--- result ---'
Get-ChildItem $out -File | Sort-Object Name | ForEach-Object {
  Write-Output ("  " + $_.Name + "  " + [math]::Round($_.Length / 1KB, 1) + " KB")
}
Write-Output 'done. 直接运行 release\payload\ImgZip.exe 即可；打包安装包请执行 installer\build-installer.ps1'
