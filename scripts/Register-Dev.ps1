#Requires -RunAsAdministrator
param(
    [string]$AssemblyPath = (Join-Path $PSScriptRoot '..\src\JocoRobos.Cad\bin\Release\net48\JocoRobos.Cad.dll')
)
$ErrorActionPreference = 'Stop'
if (![Environment]::Is64BitProcess) { throw 'Use 64-bit Windows PowerShell.' }
if (Get-Process SLDWORKS -ErrorAction SilentlyContinue) { throw 'Close SOLIDWORKS first.' }
$assembly = (Resolve-Path $AssemblyPath).Path
$regasm = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe'
if (!(Test-Path $regasm)) { throw '.NET Framework 4.x RegAsm was not found.' }
& $regasm $assembly /codebase
if ($LASTEXITCODE -ne 0) { throw "COM registration failed: $LASTEXITCODE" }
$key = 'HKLM:\SOFTWARE\SolidWorks\Addins\{E219FE9C-5919-4BE5-98B7-A518C11AD901}'
New-Item $key -Force | Out-Null
Set-Item $key -Value 0
New-ItemProperty $key -Name Title -Value 'JOCO ROBOS CAD' -PropertyType String -Force | Out-Null
New-ItemProperty $key -Name Description -Value 'Local prototype: Open Robot and callback test. SVN not connected.' -PropertyType String -Force | Out-Null
Write-Host 'Registered. In SOLIDWORKS > Tools > Add-Ins, enable JOCO ROBOS CAD and select Start Up.'
Write-Host 'Registration points to this build folder. Do not move or delete it while registered.'
