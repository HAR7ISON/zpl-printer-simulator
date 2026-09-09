[CmdletBinding()]
param([switch]$Integration)
$ErrorActionPreference = 'Stop'
$tokens = $null; $errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PSScriptRoot '../installer/Install.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
$functionAst = $ast.Find({ param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -eq 'Ensure-SimulatorPrinterPort'
}, $true)
. ([scriptblock]::Create($functionAst.Extent.Text))
if ($Integration) {
    # A unique temporary port exercises Windows itself, without touching user queues.
    $name = 'ZplSimulator-Test-' + [guid]::NewGuid().ToString('N')
    try {
        Ensure-SimulatorPrinterPort -Name $name -PortNumber 19100
        Ensure-SimulatorPrinterPort -Name $name -PortNumber 19100
        Write-Host 'PASS Windows RAW loopback port creation, SNMP disabled, and rerun'
    } finally {
        if (Get-PrinterPort -Name $name -ErrorAction SilentlyContinue) { Remove-PrinterPort -Name $name }
    }
    exit
}
# Provider doubles enforce its parameter contract and exercise repair/validation.
$script:port = $null
$script:created = 0
function Get-PrinterPort { param($Name, $ErrorAction) if ($script:port) { [pscustomobject]@{ Name = $Name } } }
function Add-PrinterPort {
    [CmdletBinding()]
    param($Name, $PrinterHostAddress, $PortNumber)
    $script:created++
    $script:port = [pscustomobject]@{ Name = $Name; HostAddress = $PrinterHostAddress; PortNumber = $PortNumber; Protocol = 1; SNMPEnabled = $true }
}
function Get-CimInstance { param($ClassName) $script:port }
function Set-CimInstance {
    param([Parameter(ValueFromPipeline)]$InputObject, $Property)
    process { $script:port.SNMPEnabled = $Property.SNMPEnabled }
}
Ensure-SimulatorPrinterPort -Name 'test' -PortNumber 19100
if ($script:port.SNMPEnabled -or $script:created -ne 1) { throw 'Port creation or SNMP disabling failed.' }
Write-Host 'PASS creation without invalid SNMP parameter, with SNMP explicitly disabled'
Ensure-SimulatorPrinterPort -Name 'test' -PortNumber 19100
if ($script:created -ne 1) { throw 'Existing port recreated.' }
Write-Host 'PASS existing port reused'
$script:port.HostAddress = '192.0.2.1'
$rejected = $false
try { Ensure-SimulatorPrinterPort -Name 'test' -PortNumber 19100 } catch { $rejected = $true }
if (-not $rejected) { throw 'Mismatched port was accepted.' }
Write-Host 'PASS mismatched port rejected'
