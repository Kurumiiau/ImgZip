# Build script for ImgZip (image compressor).
# Produces the framework-dependent build into release\payload\ — the same folder
# the installer packages (see installer\build-installer.ps1).
# Requires the .NET 8 SDK; the target machine needs the .NET 8 Desktop Runtime.
#
# Usage:  powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root 'src\ImgZip\ImgZip.csproj'
$out = Join-Path $root 'release\payload'

# Prefer the portable SDK used during development, fall back to a system install.
$sdk = 'E:\tools\dotnet8\dotnet.exe'
if (-not (Test-Path $sdk)) { $sdk = 'dotnet' }
Write-Output ("using SDK: " + $sdk)

Write-Output '--- publish ---'
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
& $sdk publish $proj -c Release -o $out --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

Write-Output '--- result ---'
Get-ChildItem $out -File | Sort-Object Name | ForEach-Object {
  Write-Output ("  " + $_.Name + "  " + [math]::Round($_.Length / 1KB, 1) + " KB")
}
Write-Output 'done. 直接运行 release\payload\ImgZip.exe 即可；打包安装包请执行 installer\build-installer.ps1'
