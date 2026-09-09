[CmdletBinding()]
param([switch]$SetupExe, [switch]$FrameworkDependent)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$package = Join-Path $root 'artifacts/ZplSimulator'
New-Item $package -ItemType Directory -Force | Out-Null
$publish = Join-Path $package 'app'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
$selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
dotnet publish (Join-Path $root 'src/ZplSimulator/ZplSimulator.csproj') -c Release -r win-x64 --self-contained $selfContained -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
Copy-Item (Join-Path $root 'installer/*.ps1'), (Join-Path $root 'installer/*.cmd'), (Join-Path $root 'installer/config.json'), (Join-Path $root 'README.md'), (Join-Path $root 'THIRD-PARTY-NOTICES.md') $package -Force
Copy-Item (Join-Path $root 'LICENSE') $package -Force
Copy-Item (Join-Path $root 'LICENSE') $publish -Force
Copy-Item (Join-Path $root 'samples') $package -Recurse -Force
Copy-Item (Join-Path $root 'scripts/Send-Zpl.ps1') $package -Force
New-Item (Join-Path $package 'scripts') -ItemType Directory -Force | Out-Null
Copy-Item (Join-Path $root 'scripts/Send-Zpl.ps1'), (Join-Path $root 'scripts/SmokeTest-Windows.ps1') (Join-Path $package 'scripts') -Force
& (Join-Path $PSScriptRoot 'Collect-Licenses.ps1') -AssetsPath (Join-Path $root 'src/ZplSimulator/obj/project.assets.json') -Destination (Join-Path $publish 'licenses')
Compress-Archive -Path "$package/*" -DestinationPath (Join-Path $root 'artifacts/ZplSimulator-win-x64.zip') -Force
if ($SetupExe) {
    $iscc = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    $compiler = if ($iscc) { $iscc.Source } else { Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe' }
    if (-not (Test-Path $compiler)) { throw 'Install Inno Setup 6 to build Setup.exe, or omit -SetupExe for the ZIP installer.' }
    & $compiler (Join-Path $root 'installer/Setup.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
}
