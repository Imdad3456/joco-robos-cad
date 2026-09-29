# JOCO ROBOS CAD — FRC 5919

A small C# add-in for SOLIDWORKS 2026 that puts SVN behind familiar CAD commands. The intended workflow is **Open Robot → Edit → CAD normally → Submit**.

**Current milestone: v0.3 — Submit.** v0.2 (Sign In, Update, Edit, Release Edit) was verified in SOLIDWORKS 2026 with the imported hexapod test assembly. Submit compiles and passes policy tests but has not yet run on Windows. Keep using disposable CAD.

Start with **[Windows setup and upgrade instructions](WINDOWS-QUICKSTART.md)**. Server administration is documented in [server/README.md](server/README.md).

## What works in this source version

The toolbar contains **Open Robot**, **Update**, **Edit**, and **Submit**. The Tools → JOCO ROBOS CAD menu also provides **Sign In**, **Test Connection**, and **Release Edit**.

- Sign In validates your account against `https://cad.imdad.stream/svn/2027-Robot/`, then saves the login in Windows Credential Manager. SVN password caching and interactive SVN prompts are disabled. Invalid TLS certificates are not accepted.
- Update checks out the complete robot on first use and subsequently uses SVN update. The workspace is `C:\JOCO-ROBOS\2027-Robot`. The repository URL and UUID are checked before use. Existing non-SVN folders are never overwritten.
- Update requires all SOLIDWORKS documents to be closed. It stops on local changes, unknown files, conflicts, switched files, or unsupported workspace items. It does not merge binary CAD or silently revert files.
- Open Robot updates first, then opens `00_Master\Robot.SLDASM` read-only. If that file does not exist, it opens the only assembly in `00_Master` that no other assembly there references (currently the hexapod's `full assembly.SLDASM`).
- Edit acts on the **active document** in its own window. Open a component separately before locking it. It checks the server revision, requests a non-stealing exclusive lock, and confirms both server ownership and the local lock token. Then it changes SOLIDWORKS to writable using `SetReadOnlyState(false)` without reloading the model.
- Release Edit only unlocks a file with no unsaved or on-disk changes. Modified files and their locks are retained.
- Submit refuses while any SOLIDWORKS document has unsaved changes. It scans the workspace with server lock status and lists **Modified** files you hold locks for, **New** CAD files (including inside new folders; `~$` owner files are ignored), and **Unchanged** locked files to release. Files it cannot submit — changed without a lock, missing/renamed, conflicted — are shown separately and left untouched. After you review and enter a comment, it checks that every submitted CAD's references are inside the workspace and that referenced new files are included. Then it rechecks status, adds new files and folders with `svn:needs-lock` and the binary MIME type, and commits the selection as one revision. It releases only the submitted files' locks, then makes those documents read-only. Unchecked files keep their edits and locks.
- An interrupted Submit is journaled. Next Submit/Update compares the server's latest bytes with your local files: an identical server copy means it landed (reported, then made clean for Update); otherwise edits and locks are kept for a retry. Mixed results stop and ask for a mentor.

Network work runs off the SOLIDWORKS UI thread in an owned modal progress window. A per-user file lock serializes add-in workspace operations across SOLIDWORKS instances. The first upgrade replaces the original two-button prototype tab once; later sessions retain customizations.

## Validation and limits

- The original local toolbar and callbacks were reported working by the user in SOLIDWORKS 2026.
- v0.3 compiles with zero warnings/errors using .NET SDK 8.0.425, .NET Framework 4.8 reference assemblies, SharpSvn 1.14005.390, and SOLIDWORKS 2024 interop 32.1.0 reference DLLs for the Linux compilation check. Those downloaded reference DLLs are not committed or distributed. The Windows build uses your installed SOLIDWORKS 2026 API DLLs.
- 20 policy tests cover path boundaries, traversal, metadata paths, symlinks, extension handling, missing/stale/wrong-user lock tokens, submittable-file filtering, new-folder scheduling, and comment validation. Run `dotnet run --project tests\WorkspacePolicy.Tests -c Release`.
- The server passed actual HTTPS checkout, commit, update, competing lock, lock stealing/breaking denial, and backup restoration tests using the native SVN client.
- Verified on Windows by the user: Sign In, Update, opening the imported hexapod, Edit, and Release Edit.
- **Not yet run on Windows:** Submit, interrupted-Submit recovery, `GetDocumentDependencies2` reference checks, and Open Robot's master-assembly fallback.
- Read-only attributes reduce mistakes; server hooks enforce commit ownership. An assembly lock does not lock its referenced parts. Offline editing and live notification of mentor-broken locks are not supported in this milestone.
- Deleting/renaming CAD, an installer, selected-component locking, and off-device backups remain to be implemented. Do not use this release for irreplaceable team edits yet.

## Windows acceptance checks

1. Close SOLIDWORKS, build/register the updated source, and restart. Verify the three-button toolbar appears without a duplicate prototype tab.
2. Use Sign In with an incorrect password: it must fail without storing that login. Use the correct password: it must validate and save successfully. Restart SOLIDWORKS and use Test Connection without another sign-in.
3. With all documents closed, click Update. An existing non-SVN robot folder must be refused unchanged; move it to a safe backup location and retry. A fresh checkout should create the canonical folders.
4. With any CAD document open, Update must refuse. With changed/unversioned files in the workspace, it must preserve them and refuse the update.
5. After a mentor imports a real disposable CAD test file with its required SVN properties, open it read-only. Click Edit: the correct user should get the lock and SOLIDWORKS should become writable without a reload.
6. In a second Windows working copy under a different account, Edit on that file must be refused and it must remain read-only. Use Release Edit on the unchanged first copy, then confirm the second account can lock it.
7. Releasing a modified file must fail without deleting changes. A network failure must never report successful lock acquisition or unlock.
8. Submit: Edit a part, change and save it, create a new part in the same folder and insert it into a locked assembly. Submit must list both, commit one revision, and leave both read-only and unlocked. A second working copy must receive them on Update.
9. Submit must refuse with unsaved documents, with a new referenced part unchecked, and with a component referenced from outside `C:\JOCO-ROBOS\2027-Robot`. Nothing may be committed in those cases.
10. Disconnect the network during Submit. Retry after reconnecting: the result must be either one committed revision or preserved edits and locks, never both or neither.

## Dependencies

The project targets .NET Framework 4.8/x64 and pins [SharpSvn 1.14005.390](https://www.nuget.org/packages/SharpSvn/1.14005.390). NuGet bundles SVN; students will not need the SVN command line. The native library requires the [Microsoft Visual C++ x64 Redistributable](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist). SOLIDWORKS API DLLs come from the installed SOLIDWORKS `api\redist` directory.

API references: [ISwAddin registration](https://help.solidworks.com/2023/English/api/sldworksapiprogguide/Overview/Using_SwAddin_to_Create_a_SolidWorks_Addin.htm), [CommandManager](https://help.solidworks.com/2021/english/api/sldworksapi/SOLIDWORKS.Interop.sldworks~SOLIDWORKS.Interop.sldworks.ICommandManager~CreateCommandGroup2.html), [SetReadOnlyState](https://help.solidworks.com/2026/English/api/sldworksapi/SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.IModelDoc2~SetReadOnlyState.html), and [SharpSvn source](https://github.com/AmpScm/SharpSvn).
