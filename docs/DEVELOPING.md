# Developer setup on Windows

Students don't need any of this: they install the newest release from [Releases](https://github.com/Imdad3456/joco-robos-cad/releases/latest). This page is for building and debugging the add-in on a Windows PC with SOLIDWORKS.

## Prerequisites

- 64-bit desktop **SOLIDWORKS 2026**.
- The **.NET 8 SDK** for Windows x64 ([download](https://dotnet.microsoft.com/en-us/download/dotnet/8.0); choose SDK, not Runtime).
- The [Microsoft Visual C++ x64 Redistributable](https://aka.ms/vs/17/release/vc_redist.x64.exe) (SharpSvn is native x64).

NuGet restores SharpSvn and the .NET Framework 4.8 reference assemblies during the build. You don't need Visual Studio, Git, TortoiseSVN, or a separate SVN install.

## Build and register a development copy

1. Close SOLIDWORKS. Download the source (**Code → Download ZIP**, unblock the ZIP in its Properties, then extract to `C:\Dev\joco-robos-cad`), or `git clone` it there.
2. In 64-bit Windows PowerShell:

   ```powershell
   cd C:\Dev\joco-robos-cad
   Get-ChildItem .\scripts\*.ps1 | Unblock-File
   Set-ExecutionPolicy -Scope Process -ExecutionPolicy RemoteSigned
   .\scripts\Build.ps1
   ```

3. In PowerShell **as administrator**, in the same folder: `.\scripts\Register-Dev.ps1`. This points SOLIDWORKS at the build folder, so keep that path stable while registered.
4. Start SOLIDWORKS. If needed, enable **JOCO ROBOS CAD** and **Start Up** in Tools → Add-Ins. The panel header shows **(dev build)**.

For a non-default SOLIDWORKS location: `.\scripts\Build.ps1 -SolidWorksInteropDir 'D:\SOLIDWORKS\api\redist'`.

To go back to the installed copy, close SOLIDWORKS, run `.\scripts\Unregister-Dev.ps1` as administrator, then reinstall the newest release. Unregistering never deletes CAD or saved credentials.

## Building the installer locally

`winget install JRSoftware.InnoSetup`, reopen PowerShell, then `.\scripts\Build-Installer.ps1`. The output is `installer\Output\JOCO-ROBOS-CAD-Setup-<version>.exe`, and the script prints its SHA-256. Normally GitHub Actions builds it for you when you push a `v*` tag (see [README](../README.md#for-mentors)).

## Troubleshooting

- **The add-in doesn't load, or SharpSvn is missing:** keep every output file (especially `SharpSvn.dll`) beside `JocoRobos.Cad.dll`, and install the x64 Visual C++ runtime.
- **Authentication failed:** Tools → JOCO ROBOS CAD → Sign In. The CAD password is separate from GitHub, Windows, and Tailscale. Saved logins are under Windows Credential Manager → Generic Credentials → JOCO ROBOS CAD.
- **Which copy is SOLIDWORKS loading?** The panel header says "(dev build)" for a development copy. From PowerShell: `(Get-ItemProperty "Registry::HKEY_CLASSES_ROOT\CLSID\{E219FE9C-5919-4BE5-98B7-A518C11AD901}\InprocServer32").CodeBase`.
- **An automatic update didn't install:** the installer log is in `%LOCALAPPDATA%\JocoRobos.Cad\updates\`.

Testing steps are in [TESTING.md](../TESTING.md).
