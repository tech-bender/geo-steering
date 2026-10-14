# Installs the .NET 8 SDK into <repo>\.dotnet (git-ignored) using Microsoft's official dotnet-install.ps1.
# No admin rights needed; nothing is installed machine-wide. build.cmd picks this SDK up automatically.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $repo '.dotnet'
$script = Join-Path ([IO.Path]::GetTempPath()) 'dotnet-install.ps1'
Invoke-WebRequest 'https://builds.dotnet.microsoft.com/dotnet/scripts/v1/dotnet-install.ps1' -OutFile $script -UseBasicParsing
$sig = Get-AuthenticodeSignature $script
if ($sig.Status -ne 'Valid') { throw "dotnet-install.ps1 signature is not valid: $($sig.Status)" }
& powershell -NoProfile -ExecutionPolicy Bypass -File $script -Channel 8.0 -InstallDir $dest -NoPath
& (Join-Path $dest 'dotnet.exe') --list-sdks
