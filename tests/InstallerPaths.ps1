$ErrorActionPreference = 'Stop'
# Exercise the installer's real parameter binding and path initialization without
# running its privileged printer/service operations.
$source = Get-Content (Join-Path $PSScriptRoot '../installer/Install.ps1') -Raw
$prefix = $source.Substring(0, $source.IndexOf('$serviceName =')) -replace '(?m)^#Requires.*\r?\n', ''
$folder = Join-Path $PSScriptRoot ('installer path test ' + [guid]::NewGuid().ToString('N'))
New-Item $folder -ItemType Directory | Out-Null
try {
    $probe = Join-Path $folder 'Probe.ps1'
    $suffix = "`n[pscustomobject]@{ Source = `$SourceDirectory; Root = `$installerDirectory; Uninstall = [bool]`$Uninstall }"
    Set-Content $probe ($prefix + $suffix)
    Push-Location $env:WINDIR
    try {
        $result = & $probe
        if ($result.Source -ne (Join-Path $folder 'app') -or $result.Root -ne $folder) { throw 'Default paths depend on working directory.' }
        Write-Host 'PASS default paths from a different working directory, with spaces'
        $result = & $probe -SourceDirectory 'C:\custom payload'
        if ($result.Source -ne 'C:\custom payload') { throw 'Explicit source was overwritten.' }
        Write-Host 'PASS explicit source directory'
        $result = & $probe -Uninstall
        if (-not $result.Uninstall) { throw 'Uninstall binding failed.' }
        Write-Host 'PASS uninstall without source argument'
        Set-Content $probe ($prefix.Replace('$installerDirectory = $PSScriptRoot', "`$installerDirectory = ''") + $suffix)
        $result = & $probe
        if ($result.Root -ne $folder) { throw 'Empty PSScriptRoot fallback failed.' }
        Write-Host 'PASS empty PSScriptRoot fallback'
    } finally { Pop-Location }
} finally { Remove-Item $folder -Recurse -Force }
