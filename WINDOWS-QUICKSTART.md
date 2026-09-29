# Windows VM setup — v0.5

This milestone adds **Submit**, automatic **seasons**, and the **parts library** on top of the verified v0.2 Sign In, Update, and Edit. It is source for Windows SOLIDWORKS 2026 testing, not the final student installer.

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

6. Start SOLIDWORKS normally. Enable **JOCO ROBOS CAD** and **Start Up** in Tools → Add-Ins if necessary. The toolbar should show **Open Robot**, **Update**, **Edit**, **Submit**, and **Insert from Library**. The layout is rebuilt once after this upgrade. With no document open, use **Tools → JOCO ROBOS CAD**.
7. Choose **Sign In**, enter username `imdad` and the CAD password you created. Use **Test Connection** to verify. The password is stored only in Windows Credential Manager after successful server authentication.
8. Save and close all CAD documents, then click **Update**. It updates `C:\JOCO-ROBOS\2027-Robot` and downloads the new `C:\JOCO-ROBOS\Library`. **Open Robot** should open the hexapod's `full assembly.SLDASM` read-only.

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

## Testing Submit with the hexapod

1. Click **Open Robot**. Open `coxa.SLDPRT` in its own window and click **Edit**. Change a dimension and **save**.
2. Open `leg.SLDASM` in its own window and click **Edit**. Create a new part, save it as `C:\JOCO-ROBOS\2027-Robot\00_Master\TestSpacer.SLDPRT`, insert it into `leg.SLDASM`, and save the assembly.
3. Click **Submit**. Expect **Modified:** `coxa.SLDPRT`, `leg.SLDASM` and **New:** `TestSpacer.SLDPRT`. Enter a comment and submit. Expect "Submitted as revision 3" and all three documents to become read-only.
4. Negative checks, each of which must commit nothing: submit with an unsaved change; uncheck `TestSpacer.SLDPRT` while `leg.SLDASM` is checked; insert a part saved on the Desktop into a locked assembly.
5. Retry Edit on `coxa.SLDPRT`. It should lock again because Submit released it.

## Testing the library

1. In a browser, open **https://cad.imdad.stream/admin** and sign in with your CAD account. On **Library**, upload a small test part (for example a bolt) into `Hardware`.
2. In SOLIDWORKS, open `leg.SLDASM`, click **Edit**, then **Insert from Library** and choose the bolt. It should be copied to `2027-Robot\90_COTS\Hardware\` and inserted at the origin.
3. Save and **Submit**. The bolt should be listed as New with the modified leg.

## Testing seasons

On the admin page, **Seasons → Start a new season** creates `2028-Robot`. Leave "Make it active" unchecked until you want to try it. When it's active, Open Robot switches to `C:\JOCO-ROBOS\2028-Robot` by itself; **Tools → JOCO ROBOS CAD → Choose Robot** can go back to 2027. Make 2027 active again afterward. An unused test season can't be deleted from the page yet; ask me to remove it.

**Release Edit** in the Tools menu still unlocks an unchanged file without submitting. If a file is changed, keep your work and lock; do not manually delete or revert it.

## Troubleshooting

- **Missing SharpSvn or wrong architecture:** rebuild with the provided project and retain all output files, especially `SharpSvn.dll`, beside `JocoRobos.Cad.dll`. Do not copy only the add-in DLL. Install the x64 Visual C++ Redistributable if a native dependency is missing.
- **Authentication failed:** use Sign In again. Your CAD password is separate from GitHub, Windows, and Tailscale.
- **Update refused:** save/close documents, then read the reported local-file issue. The add-in does not discard changes.
- **Locked by someone else:** inspect the file read-only or contact its owner. Do not remove read-only attributes manually.
- **Lock acquired but document stayed read-only:** your lock is retained. Close/reopen the document and retry Edit; record the exact error if it persists.

For removing the developer add-in, close SOLIDWORKS and run `.\scripts\Unregister-Dev.ps1` as administrator before deleting the build folder. Unregistering does not delete CAD or saved credentials. Saved credentials can be removed under Windows Credential Manager → Windows Credentials → Generic Credentials → JOCO ROBOS CAD.

Follow the [full acceptance checklist](README.md#windows-acceptance-checks). Submit has not yet been run on the Windows VM; record exact error text if a step fails.
