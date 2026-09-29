# Windows VM setup — v0.2

This milestone adds **sign-in, Update, and Edit**. It is source for Windows SOLIDWORKS 2026 testing, not the final student installer. Submit and initial CAD import are still pending.

## Upgrade your existing prototype

1. **Close SOLIDWORKS.** Download the latest source using **Code → Download ZIP** from [the private repository](https://github.com/Imdad3456/joco-robos-cad) while signed in to GitHub.
2. Right-click the ZIP → Properties → Unblock if shown, then extract it. Copy the updated source into your existing `C:\Dev\joco-robos-cad` folder. Keep the registered build path stable. If you cloned with Git, use `git pull` instead.
3. Install the [Microsoft Visual C++ v14 x64 Redistributable](https://aka.ms/vc14/vc_redist.x64.exe) if it is not already installed. SharpSvn is a native x64 dependency. See [Microsoft's download documentation](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist).
4. Open regular **64-bit Windows PowerShell** and build:

   ```powershell
   cd C:\Dev\joco-robos-cad
   Get-ChildItem .\scripts\*.ps1 | Unblock-File
   Set-ExecutionPolicy -Scope Process -ExecutionPolicy RemoteSigned
   .\scripts\Build.ps1
   ```

5. In **Windows PowerShell as administrator**, register the new build:

   ```powershell
   cd C:\Dev\joco-robos-cad
   Set-ExecutionPolicy -Scope Process -ExecutionPolicy RemoteSigned
   .\scripts\Register-Dev.ps1
   ```

6. Start SOLIDWORKS normally. Enable **JOCO ROBOS CAD** and **Start Up** in Tools → Add-Ins if necessary. The toolbar should show **Open Robot**, **Update**, and **Edit**. With no document open, use **Tools → JOCO ROBOS CAD**.
7. Choose **Sign In**, enter username `imdad` and the CAD password you created. Use **Test Connection** to verify. The password is stored only in Windows Credential Manager after successful server authentication.
8. Save and close all CAD documents, then click **Update**. The server currently contains only subsystem folders, so Open Robot will report that the master assembly has not been uploaded yet. That is expected.

**If the prototype already put test CAD in `C:\JOCO-ROBOS\2027-Robot`:** move that whole folder to a safe backup location such as `C:\JOCO-ROBOS\2027-Robot-local-backup` before the first checkout. Do not delete it. Update deliberately refuses to overwrite a nonempty folder without SVN metadata. This version does not upload those files automatically.

## First-time developer prerequisites

- A working installation of **64-bit desktop SOLIDWORKS 2026**.
- The **Windows x64 SDK** from Microsoft's [.NET 8 download page](https://dotnet.microsoft.com/en-us/download/dotnet/8.0). Choose SDK, not just Runtime.
- The **Visual C++ x64 Redistributable** linked above.

NuGet restores SharpSvn and .NET Framework 4.8 compilation references during the build. A separately installed .NET Framework Developer Pack is no longer necessary for compilation. Windows/SOLIDWORKS must still have the .NET Framework 4.8 runtime. You do not need Visual Studio, Git, TortoiseSVN, or a separate SVN install for these steps.

For a non-default SOLIDWORKS installation, provide its API DLL directory:

```powershell
.\scripts\Build.ps1 -SolidWorksInteropDir 'D:\SOLIDWORKS\api\redist'
```

The directory must contain `SolidWorks.Interop.sldworks.dll`, `SolidWorks.Interop.swconst.dll`, and `SolidWorks.Interop.swpublished.dll`.

## Testing Edit later

Once a mentor uploads a disposable CAD file with the required SVN properties, Update to download it, open it in its own window, and click Edit. It should become writable only after ownership is verified. An assembly lock does not lock the parts inside it.

**Release Edit** is in the Tools menu and only works for an unchanged file. It exists to test acquiring and releasing a lock before Submit is implemented. If you change a file, keep your work and lock; do not manually delete or revert it to make the test pass.

## Troubleshooting

- **Missing SharpSvn or wrong architecture:** rebuild with the provided project and retain all output files, especially `SharpSvn.dll`, beside `JocoRobos.Cad.dll`. Do not copy only the add-in DLL. Install the x64 Visual C++ Redistributable if a native dependency is missing.
- **Authentication failed:** use Sign In again. Your CAD password is separate from GitHub, Windows, and Tailscale.
- **Update refused:** save/close documents, then read the reported local-file issue. The add-in does not discard changes.
- **Locked by someone else:** inspect the file read-only or contact its owner. Do not remove read-only attributes manually.
- **Lock acquired but document stayed read-only:** your lock is retained. Close/reopen the document and retry Edit; record the exact error if it persists.

For removing the developer add-in, close SOLIDWORKS and run `.\scripts\Unregister-Dev.ps1` as administrator before deleting the build folder. Unregistering does not delete CAD or saved credentials. Saved credentials can be removed under Windows Credential Manager → Windows Credentials → Generic Credentials → JOCO ROBOS CAD.

Follow the [full acceptance checklist](README.md#windows-acceptance-checks). The new native runtime and SOLIDWORKS mode changes have not yet been run on the Windows VM.
