# Set up the Windows VM

This is the first developer prototype, not the final student installer. It must be compiled and tested on Windows with SOLIDWORKS. SVN collaboration is not implemented yet.

## 1. Install prerequisites

- Install your licensed **64-bit desktop SOLIDWORKS** in the VM and confirm it starts.
- Install the **Windows x64 SDK** from Microsoft's [.NET 8 download page](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) (choose SDK, not just Runtime).
- Install the **Developer Pack** from Microsoft's [.NET Framework 4.8 download page](https://dotnet.microsoft.com/en-us/download/dotnet-framework/net48).

You do not need Visual Studio for these build commands. Open a fresh PowerShell window after installing the SDK.

## 2. Download this project

Sign in to GitHub with access to the private repository, open [Imdad3456/joco-robos-cad](https://github.com/Imdad3456/joco-robos-cad), and select **Code → Download ZIP**.

Right-click the downloaded ZIP → **Properties** → **Unblock** if shown → Apply, then extract it. Move the folder containing this file to `C:\Dev\joco-robos-cad`. Keep that path stable after registration.

## 3. Build

Open regular **Windows PowerShell** and run:

```powershell
cd C:\Dev\joco-robos-cad
dotnet --info
.\scripts\Build.ps1
```

If PowerShell blocks local scripts, allow local scripts for this window only, then rerun the build:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy RemoteSigned
```

If SOLIDWORKS is installed somewhere else, provide the directory containing its three `SolidWorks.Interop.*.dll` API assemblies:

```powershell
.\scripts\Build.ps1 -SolidWorksInteropDir 'D:\SOLIDWORKS\api\redist'
```

## 4. Register and load

Close SOLIDWORKS. Open **Windows PowerShell as administrator** (64-bit, not the x86 shortcut):

```powershell
cd C:\Dev\joco-robos-cad
Set-ExecutionPolicy -Scope Process -ExecutionPolicy RemoteSigned
.\scripts\Register-Dev.ps1
```

Start SOLIDWORKS normally. In **Tools → Add-Ins**, check **JOCO ROBOS CAD** and **Start Up**. Open a document, select the JOCO ROBOS CAD tab, and click **Test Callback**.

## 5. Try Open Robot

Put a disposable assembly copy and its referenced components in the local workspace, with the master at:

```text
C:\JOCO-ROBOS\2027-Robot\00_Master\Robot.SLDASM
```

Click **Open Robot**. The top-level assembly should open read-only. The prototype does not enforce locks on referenced parts and does not download or upload CAD. Follow the full [acceptance checklist](README.md#windows-acceptance-checklist) before proceeding to SVN development.

## Updating or removing the prototype

Close SOLIDWORKS before replacing the source and rebuilding. Keep the same folder so COM registration continues to find the DLL. If moving the project, unregister from the old location first, then build and register at the new location.

To remove it, close SOLIDWORKS and run this in elevated Windows PowerShell **before deleting the project folder**:

```powershell
cd C:\Dev\joco-robos-cad
.\scripts\Unregister-Dev.ps1
```

If a step fails, retain the exact error text and note the SOLIDWORKS year/service pack. The Linux authoring environment could not compile or run this Windows add-in.
