# Builds installer\Output\JOCO-ROBOS-CAD-Setup-<version>.exe on a Windows developer PC.
param(
    [string]$SolidWorksInteropDir = "$env:ProgramW6432\SOLIDWORKS Corp\SOLIDWORKS\api\redist"
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
& (Join-Path $PSScriptRoot 'Build.ps1') -SolidWorksInteropDir $SolidWorksInteropDir

$redist = Join-Path $root 'installer\redist\vc_redist.x64.exe'
if (!(Test-Path $redist)) {
    New-Item -ItemType Directory -Force (Split-Path $redist) | Out-Null
    Write-Host 'Downloading the Microsoft Visual C++ x64 runtime to bundle…'
    Invoke-WebRequest 'https://aka.ms/vs/17/release/vc_redist.x64.exe' -OutFile $redist -UseBasicParsing
}
$signature = Get-AuthenticodeSignature $redist
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation') {
    Remove-Item $redist
    throw 'The downloaded Visual C++ runtime is not signed by Microsoft. It was deleted; try again.'
}

$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (!$iscc) { throw 'Install Inno Setup 6 first:  winget install JRSoftware.InnoSetup  (then reopen PowerShell)' }

[xml]$project = Get-Content (Join-Path $root 'src\JocoRobos.Cad\JocoRobos.Cad.csproj')
$version = ($project.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
& $iscc "/DAppVersion=$version" (Join-Path $root 'installer\JocoRobosCad.iss')
if ($LASTEXITCODE -ne 0) { throw "Installer build failed: $LASTEXITCODE" }
Write-Host "Built installer\Output\JOCO-ROBOS-CAD-Setup-$version.exe"
