[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$config = Get-Content "$env:ProgramData\ZplSimulator\config.json" -Raw | ConvertFrom-Json
if (-not $config.PrintToFile -or $config.TargetPrinter -ne 'Microsoft Print to PDF') { throw 'This check requires Microsoft Print to PDF with PrintToFile enabled.' }
if ((Get-Service ZplSimulator).Status -ne 'Running') { throw 'Install/start the service before testing.' }
$existing = @(Get-ChildItem $config.OutputDirectory -Filter '*.pdf' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name)
$root = Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot 'Send-Zpl.ps1') -Path (Join-Path $root 'samples/test-label.zpl') -PrinterName $config.PrinterName
$deadline = (Get-Date).AddSeconds(90)
do {
    Start-Sleep -Milliseconds 500
    $pdf = Get-ChildItem $config.OutputDirectory -Filter '*.pdf' -ErrorAction SilentlyContinue | Where-Object { $_.Name -notin $existing -and $_.Length -gt 100 } | Select-Object -First 1
} while (-not $pdf -and (Get-Date) -lt $deadline)
if (-not $pdf) { throw "No PDF appeared. Check $env:ProgramData\ZplSimulator\service.log and Failed." }
$bytes = [IO.File]::ReadAllBytes($pdf.FullName)
if ([Text.Encoding]::ASCII.GetString($bytes, 0, 5) -ne '%PDF-') { throw 'Output does not have a PDF header.' }
Write-Host "PASS: Windows RAW queue -> service -> Microsoft Print to PDF: $($pdf.FullName)"
