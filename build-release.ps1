# Builds the release zip: one self-contained exe (no .NET install needed) plus the helper files.
# Usage: .\build-release.ps1            -> dist\PulseOfTime-<version>-win-x64.zip
param([string]$Version = ([xml](Get-Content "$PSScriptRoot\PulseOfTime.csproj")).Project.PropertyGroup.Version)
$ErrorActionPreference = 'Stop'
$out = Join-Path $PSScriptRoot 'dist'
$stage = Join-Path $out "PulseOfTime-$Version"
Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue
dotnet publish "$PSScriptRoot\PulseOfTime.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none -p:Version=$Version -o $stage
if ($LASTEXITCODE -ne 0) { throw "publish failed" }
Copy-Item "$PSScriptRoot\package\*", "$PSScriptRoot\README.md", "$PSScriptRoot\LICENSE" $stage
$zip = Join-Path $out "PulseOfTime-$Version-win-x64.zip"
Compress-Archive -Path "$stage\*" -DestinationPath $zip
Write-Host "Done: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)"
