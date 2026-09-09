#Requires -RunAsAdministrator
[CmdletBinding()]
param([switch]$Uninstall, [string]$SourceDirectory)
$ErrorActionPreference = 'Stop'
# Resolve paths after parameter binding; automatic script variables may be empty
# while PowerShell evaluates parameter defaults.
$installerDirectory = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($installerDirectory)) {
    $scriptPath = $PSCommandPath
    if ([string]::IsNullOrWhiteSpace($scriptPath)) { $scriptPath = $MyInvocation.MyCommand.Path }
    if ([string]::IsNullOrWhiteSpace($scriptPath)) {
        throw 'Cannot locate the installer. Run Install.cmd or invoke Install.ps1 using powershell.exe -File.'
    }
    $installerDirectory = Split-Path -LiteralPath $scriptPath
}
if ([string]::IsNullOrWhiteSpace($SourceDirectory)) {
    $SourceDirectory = Join-Path $installerDirectory 'app'
}
$serviceName = 'ZplSimulator'
$installDirectory = Join-Path $env:ProgramFiles 'ZplSimulator\app'
$dataDirectory = Join-Path $env:ProgramData 'ZplSimulator'
$statePath = Join-Path $dataDirectory 'install-state.json'
$configPath = Join-Path $dataDirectory 'config.json'
$state = if (Test-Path $statePath) { Get-Content $statePath -Raw | ConvertFrom-Json } else { $null }

function Ensure-SimulatorPrinterPort {
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][uint32]$PortNumber)
    if (-not (Get-PrinterPort -Name $Name -ErrorAction SilentlyContinue)) {
        # -SNMP is a device index that enables SNMP, not an on/off switch.
        # Passing 0 is rejected by some versions of the Windows port provider.
        Add-PrinterPort -Name $Name -PrinterHostAddress '127.0.0.1' -PortNumber $PortNumber
    }
    $tcpPort = Get-CimInstance -ClassName Win32_TCPIPPrinterPort | Where-Object { $_.Name -eq $Name }
    if (-not $tcpPort -or $tcpPort.HostAddress -ne '127.0.0.1' -or
        $tcpPort.PortNumber -ne $PortNumber -or $tcpPort.Protocol -ne 1) {
        throw "Printer port '$Name' does not match the required local RAW TCP configuration."
    }
    if ($tcpPort.SNMPEnabled) {
        $tcpPort | Set-CimInstance -Property @{ SNMPEnabled = $false } | Out-Null
        $verified = Get-CimInstance -ClassName Win32_TCPIPPrinterPort | Where-Object { $_.Name -eq $Name }
        if (-not $verified -or $verified.SNMPEnabled) { throw "Could not disable SNMP on '$Name'." }
    }
}

function Remove-OwnedResources {
    if (-not $state) { throw 'No installation record found; no resources were removed.' }
    $service = Get-Service $serviceName -ErrorAction SilentlyContinue
    if ($service) {
        Stop-Service $serviceName -Force
        $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
        & sc.exe delete $serviceName | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Could not delete service.' }
    }
    $queue = Get-Printer -Name $state.PrinterName -ErrorAction SilentlyContinue
    if ($queue -and $queue.PortName -eq $state.PortName) { Remove-Printer -Name $state.PrinterName }
    if (Get-PrinterPort -Name $state.PortName -ErrorAction SilentlyContinue) {
        Remove-PrinterPort -Name $state.PortName
    }
}

if ($Uninstall) {
    Remove-OwnedResources
    if (Test-Path $installDirectory) { Remove-Item $installDirectory -Recurse -Force }
    Remove-Item $statePath
    Write-Host "Uninstalled. Config, PDFs, and failed jobs remain in $dataDirectory."
    exit
}

if (-not [Environment]::Is64BitProcess) { throw 'Use 64-bit PowerShell.' }
if (-not (Test-Path (Join-Path $SourceDirectory 'ZplSimulator.exe'))) { throw "Missing app payload in $SourceDirectory. Build or extract the complete installer first." }
New-Item $dataDirectory -ItemType Directory -Force | Out-Null
if (-not (Test-Path $configPath)) { Copy-Item (Join-Path $installerDirectory 'config.json') $configPath }
$config = Get-Content $configPath -Raw | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace($config.PrinterName) -or [string]::IsNullOrWhiteSpace($config.TargetPrinter) -or $config.PrinterName -eq $config.TargetPrinter) {
    throw 'Input and target printers must have different, nonempty names.'
}
if ($config.Port -lt 1024 -or $config.Port -gt 65535) { throw 'Port must be between 1024 and 65535.' }
$target = Get-Printer -Name $config.TargetPrinter -ErrorAction SilentlyContinue
if (-not $target -and $config.TargetPrinter -eq 'Microsoft Print to PDF') {
    Enable-WindowsOptionalFeature -Online -FeatureName Printing-PrintToPDFServices-Features -All -NoRestart | Out-Null
    $target = Get-Printer -Name $config.TargetPrinter -ErrorAction SilentlyContinue
}
if (-not $target) { throw "Target printer '$($config.TargetPrinter)' is missing. Install it in Windows, restart if prompted, and run Install again." }
$portName = "ZplSimulator-$($config.Port)"
$existing = Get-Printer -Name $config.PrinterName -ErrorAction SilentlyContinue
if ($existing -and (-not $state -or $existing.Name -ne $state.PrinterName -or $existing.PortName -ne $state.PortName)) {
    throw 'An unrelated printer already uses this name. Choose another PrinterName in config.json.'
}
if ((Get-Service $serviceName -ErrorAction SilentlyContinue) -and -not $state) { throw 'A service named ZplSimulator already exists without our installation record.' }
if ((Get-PrinterPort -Name $portName -ErrorAction SilentlyContinue) -and (-not $state -or $state.PortName -ne $portName)) { throw 'An unrelated printer port already uses the requested name.' }
Add-PrinterDriver -Name 'Generic / Text Only'

# Program files and configuration are writable only by administrators/SYSTEM.
# Use SID-based ACLs so this also works on non-English Windows installations.
$acl = New-Object System.Security.AccessControl.DirectorySecurity
$acl.SetAccessRuleProtection($true, $false)
foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
    $identity = New-Object System.Security.Principal.SecurityIdentifier($sid)
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($identity, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
}
$users = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-545')
$acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($users, 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
Set-Acl $dataDirectory $acl

if ($state) {
    if (($state.PrinterName -ne $config.PrinterName -or $state.PortName -ne $portName) -and
        (Get-Printer -Name $state.PrinterName -ErrorAction SilentlyContinue) -and
        (Get-PrintJob -PrinterName $state.PrinterName)) { throw 'Clear pending jobs before changing the input printer name or port.' }
    $service = Get-Service $serviceName -ErrorAction SilentlyContinue
    if ($service) { Stop-Service $serviceName -Force; $service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30)) }
    if ($state.PrinterName -ne $config.PrinterName -or $state.PortName -ne $portName) {
        $oldQueue = Get-Printer -Name $state.PrinterName -ErrorAction SilentlyContinue
        if ($oldQueue -and $oldQueue.PortName -eq $state.PortName) {
            if (Get-PrintJob -PrinterName $state.PrinterName) { throw 'Clear pending jobs before changing the input printer name or port.' }
            Remove-Printer -Name $state.PrinterName
        }
        if ($state.PortName -ne $portName -and (Get-PrinterPort -Name $state.PortName -ErrorAction SilentlyContinue)) { Remove-PrinterPort -Name $state.PortName }
    }
}
New-Item $installDirectory -ItemType Directory -Force | Out-Null
if ([IO.Path]::GetFullPath($SourceDirectory).TrimEnd('\') -ne [IO.Path]::GetFullPath($installDirectory).TrimEnd('\')) {
    Copy-Item (Join-Path $SourceDirectory '*') $installDirectory -Recurse -Force
}
# Record ownership before mutations so a partially completed install is repairable.
@{ PrinterName = $config.PrinterName; PortName = $portName } | ConvertTo-Json | Set-Content $statePath
Ensure-SimulatorPrinterPort -Name $portName -PortNumber ([uint32]$config.Port)
if (-not (Get-Printer -Name $config.PrinterName -ErrorAction SilentlyContinue)) {
    Add-Printer -Name $config.PrinterName -DriverName 'Generic / Text Only' -PortName $portName -PrintProcessor 'winprint' -Datatype 'RAW'
}
if (-not (Get-Service $serviceName -ErrorAction SilentlyContinue)) {
    New-Service -Name $serviceName -DisplayName 'ZPL Simulator' -BinaryPathName ('"' + (Join-Path $installDirectory 'ZplSimulator.exe') + '"') -StartupType Automatic | Out-Null
}
& sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not configure service recovery.' }
Start-Service $serviceName
Start-Sleep -Seconds 2
if ((Get-Service $serviceName).Status -ne 'Running') { throw "Service did not stay running. Check $dataDirectory\service.log." }
$probe = New-Object System.Net.Sockets.TcpClient
try { $probe.Connect('127.0.0.1', [int]$config.Port) } finally { $probe.Dispose() }
Write-Host "Installed printer '$($config.PrinterName)' -> '$($config.TargetPrinter)'."
Write-Host "Configuration: $configPath"
Write-Host "PDF output: $($config.OutputDirectory)"
