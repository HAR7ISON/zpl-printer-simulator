param([Parameter(Mandatory)][string]$AssetsPath, [Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'
$assets = Get-Content $AssetsPath -Raw | ConvertFrom-Json
New-Item $Destination -ItemType Directory -Force | Out-Null
foreach ($library in $assets.libraries.PSObject.Properties) {
    if ($library.Value.type -ne 'package') { continue }
    $relative = $library.Value.path
    $packagePath = $null
    foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
        $candidate = Join-Path $folder $relative
        if (Test-Path $candidate) { $packagePath = $candidate; break }
    }
    if (-not $packagePath) { throw "Missing restored package: $relative" }
    $target = Join-Path $Destination $relative
    foreach ($file in Get-ChildItem $packagePath -Recurse -File | Where-Object { $_.Name -match '(?i)license|notice|copying|\.nuspec$' }) {
        $suffix = $file.FullName.Substring($packagePath.TrimEnd('\', '/').Length).TrimStart('\', '/')
        $output = Join-Path $target $suffix
        New-Item (Split-Path $output -Parent) -ItemType Directory -Force | Out-Null
        Copy-Item $file.FullName $output -Force
    }
}
