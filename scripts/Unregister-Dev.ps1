#Requires -RunAsAdministrator
param(
    [string]$AssemblyPath = (Join-Path $PSScriptRoot '..\src\JocoRobos.Cad\bin\Release\net48\JocoRobos.Cad.dll')
)
$ErrorActionPreference = 'Stop'
if (![Environment]::Is64BitProcess) { throw 'Use 64-bit Windows PowerShell.' }
if (Get-Process SLDWORKS -ErrorAction SilentlyContinue) { throw 'Close SOLIDWORKS first.' }
$assembly = (Resolve-Path $AssemblyPath).Path
$regasm = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe'
& $regasm $assembly /unregister
if ($LASTEXITCODE -ne 0) { throw "COM unregistration failed: $LASTEXITCODE" }
$guid = '{E219FE9C-5919-4BE5-98B7-A518C11AD901}'
foreach ($key in @("HKLM:\SOFTWARE\SolidWorks\Addins\$guid", "HKCU:\Software\SolidWorks\AddInsStartup\$guid")) {
    if (Test-Path $key) { Remove-Item $key -Recurse -Force }
}
Write-Host 'Unregistered. CAD files have not been changed.'
