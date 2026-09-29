param(
    [string]$SolidWorksInteropDir = "$env:ProgramW6432\SOLIDWORKS Corp\SOLIDWORKS\api\redist"
)
$ErrorActionPreference = 'Stop'
if (Get-Process SLDWORKS -ErrorAction SilentlyContinue) { throw 'Close SOLIDWORKS before rebuilding the registered add-in.' }
$project = Join-Path $PSScriptRoot '..\src\JocoRobos.Cad\JocoRobos.Cad.csproj'
foreach ($assembly in @('sldworks', 'swconst', 'swpublished')) {
    if (!(Test-Path (Join-Path $SolidWorksInteropDir "SolidWorks.Interop.$assembly.dll"))) {
        throw "Missing SOLIDWORKS interop assembly: $assembly. Supply -SolidWorksInteropDir."
    }
}
if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'Install the Windows x64 .NET SDK on this developer PC and reopen PowerShell.'
}
& dotnet build $project --configuration Release "-p:SolidWorksInteropDir=$SolidWorksInteropDir"
if ($LASTEXITCODE -ne 0) { throw "Build failed: $LASTEXITCODE" }
Write-Host 'Build succeeded. Close SOLIDWORKS before registering the add-in.'
