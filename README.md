# JOCO ROBOS CAD — FRC 5919

**Setting up the Windows VM? Start with [WINDOWS-QUICKSTART.md](WINDOWS-QUICKSTART.md)** for downloads and copy/paste commands. No Git installation is needed to download the source.

The Steam Deck backend now has its own [server setup and operations guide](server/README.md). The Windows add-in is not connected to it yet.

Milestones 1–4 starter: a C# SOLIDWORKS add-in with a JOCO ROBOS CAD CommandManager tab, **Test Callback**, and **Open Robot**. Targets 64-bit desktop SOLIDWORKS and .NET Framework 4.8. The intended SOLIDWORKS release still needs to be verified on the team's Windows PC.

**Status:** source prepared; not compiled or run in SOLIDWORKS. The authoring environment is Linux without SOLIDWORKS, its API assemblies, or a .NET compiler. This is a developer prototype, not a student installer or a working collaboration system.

## What this version does

- Implements `ISwAddin`, registers COM callbacks, and creates a menu/toolbar and document tabs.
- Provides **Test Callback** to confirm that a button reaches the C# code.
- Opens `C:\JOCO-ROBOS\2027-Robot\00_Master\Robot.SLDASM` using SOLIDWORKS' read-only open option.
- Reports a missing assembly or load diagnostics, and leaves an already-open robot untouched.
- Preserves existing CommandManager tabs across unload/reload.

The prototype has no Update, Edit, Submit, credentials, network calls, or SVN dependency. Read-only opening of the top-level assembly is not workspace-wide enforcement: referenced components and other documents are not protected by this prototype. Do not use it to coordinate shared production CAD yet.

## Build on one Windows developer PC

Prerequisites: 64-bit desktop SOLIDWORKS, a .NET SDK capable of building `net48`, and the .NET Framework 4.8 Developer Pack. These are developer prerequisites only; the eventual student installer will bundle the add-in and runtime dependencies.

1. Extract this folder to a stable local path, for example `C:\Dev\JOCO-ROBOS-CAD`.
2. Open Windows PowerShell in this folder and build:

   ```powershell
   .\scripts\Build.ps1
   ```

   For a non-default SOLIDWORKS installation:

   ```powershell
   .\scripts\Build.ps1 -SolidWorksInteropDir 'D:\SOLIDWORKS\api\redist'
   ```

   The directory must contain `SolidWorks.Interop.sldworks.dll`, `SolidWorks.Interop.swconst.dll`, and `SolidWorks.Interop.swpublished.dll` from your SOLIDWORKS installation. No proprietary API DLLs are included in this package.

3. Close SOLIDWORKS. In **64-bit Windows PowerShell running as administrator**, register:

   ```powershell
   .\scripts\Register-Dev.ps1
   ```

   This uses .NET Framework's 64-bit RegAsm and adds this add-in's machine-wide SOLIDWORKS discovery key. RegAsm may warn about `/codebase` with an unsigned assembly; signing is deferred. Registration points at the build output, so keep it in place. No type library is generated or needed for this callback prototype.

4. Start SOLIDWORKS as your normal Windows user. Open **Tools → Add-Ins**, enable **JOCO ROBOS CAD**, and select **Start Up**. Startup is selected manually for this developer milestone; the final installer will configure the intended student's profile.
5. Open any part, assembly, or drawing to see the **JOCO ROBOS CAD** CommandManager tab. With no document open, use the add-in menu under Tools or enable its toolbar through SOLIDWORKS toolbar customization.

Use a disposable robot assembly and its referenced files for the first check. SOLIDWORKS Pack and Go can gather a representative test copy; arrange the top-level assembly at the fixed path above and check that its references resolve. The package does not contain a fabricated `.SLDASM` file.

## Windows acceptance checklist

All items below are **pending**, not claimed test results. Record the SOLIDWORKS year/service pack and Windows version when running them.

- Build finishes with zero errors using the installed SOLIDWORKS API assemblies.
- Registration succeeds and the add-in appears in Tools → Add-Ins.
- Loading it creates the tab in part, assembly, and drawing documents.
- **Test Callback** displays “the callback works.”
- **Open Robot**, before the test assembly exists, displays the expected path without crashing.
- With a valid test assembly in place, **Open Robot** opens it and the top-level assembly reports read-only in SOLIDWORKS. Verify referenced components resolve.
- Clicking **Open Robot** again does not close, reload, or discard edits in the existing document.
- Unloading and reloading the add-in does not duplicate tabs; both callbacks still work.
- Restarting SOLIDWORKS with Start Up selected loads the add-in and working commands.
- Close SOLIDWORKS and run `.\scripts\Unregister-Dev.ps1` as administrator. The add-in disappears from Add-Ins; CAD files remain untouched. Run unregister before deleting the build folder. If elevated using another administrator account, startup cleanup applies to that account; clear Start Up as the original user first.

If a callback does nothing, confirm the add-in loaded, that the matching x64 DLL was registered, and that the old DLL was not left loaded during a rebuild. Public callback methods are exposed through an explicit COM dispatch interface.

## Files

- `src/JocoRobos.Cad/Addin.cs`: COM add-in, command creation, callbacks, local assembly opening.
- `src/JocoRobos.Cad/JocoRobos.Cad.csproj`: x64 .NET Framework project with local SOLIDWORKS API references.
- `scripts/`: developer build, registration, and unregistration.
- `docs/NEXT-MILESTONES.md`: planned SVN behavior and verification gates.

## API references

The add-in uses the documented [ISwAddin registration model](https://help.solidworks.com/2023/English/api/sldworksapiprogguide/Overview/Using_SwAddin_to_Create_a_SolidWorks_Addin.htm), [CommandManager tabs and callbacks](https://help.solidworks.com/2023/English/api/sldworksapi/Create_CommandManager_Tab_and_Tab_Boxes_Example_CSharp.htm), and [OpenDoc6](https://help.solidworks.com/2025/English/api/sldworksapi/SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.ISldWorks~OpenDoc6.html). The command group ID must change if the command layout changes; see [CreateCommandGroup2](https://help.solidworks.com/2021/english/api/sldworksapi/SOLIDWORKS.Interop.sldworks~SOLIDWORKS.Interop.sldworks.ICommandManager~CreateCommandGroup2.html).
