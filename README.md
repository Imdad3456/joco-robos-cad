# JOCO ROBOS CAD — FRC 5919

A small C# add-in for SOLIDWORKS 2026 that puts SVN behind familiar CAD commands. The intended workflow is **Open Robot → Edit → CAD normally → Submit**.

**Current milestone: v0.6 — automatic add-in updates and GitHub builds.**

**Releasing a new add-in version.** Bump `<Version>` in `src/JocoRobos.Cad/JocoRobos.Cad.csproj` and push a matching tag (`git tag v0.7.0 && git push --tags`). GitHub Actions builds the installer, attaches it to a GitHub Release, and **stages** it on the server with its SHA-256. The upload uses the `github-release` account: it can only stage installers, SVN denies it every repository, and its password lives only on the Deck and in the `JOCO_PUBLISH_PASSWORD` repository secret. On **https://cad.imdad.stream/admin/addin**, click **Release to students** (or **Release as required**). Manual upload remains as a fallback; paste the release's SHA-256 so a partial download is refused. Each add-in checks the server when SOLIDWORKS starts and every 3 minutes. When it finds a newer version, it asks the student once and shows **Install update** in the pane. It downloads the installer and checks its SHA-256 against the value mentors published. The installer asks Windows for admin approval, waits for the student to close SOLIDWORKS, installs silently (logging to `%LOCALAPPDATA%\JocoRobos.Cad\updates\*.log`), and reopens SOLIDWORKS. If the next start still shows the old version, the add-in says so and points to the log. A required update blocks Edit, Submit, and Insert from Library until it's installed. Anyone with a mentor account can push code to every student PC this way: keep mentor accounts few, with strong passwords.

**Automated builds.** `.github/workflows/build.yml` runs on every push. It builds the server container and runs the mentor-page and lock/backup tests. It also runs the add-in policy tests and, once the SOLIDWORKS API DLLs are in `lib/solidworks` (see its README), builds the installer.

**v0.5 — installer, status pane, component Edit.** Everything below v0.2 in this list is compiled and unit-tested but not yet run on Windows; follow **[TESTING.md](TESTING.md)**.

**v0.4 — seasons and the parts library.** v0.2 (Sign In, Update, Edit, Release Edit) was verified in SOLIDWORKS 2026 with the imported hexapod. Submit (v0.3), season switching, and Insert from Library compile and pass policy tests, but have not yet run on Windows. Keep using disposable CAD.

Mentors manage seasons, the library, locks, and accounts at **https://cad.imdad.stream/admin** (see [server/README.md](server/README.md)). Students only use SOLIDWORKS.

Start with **[Windows setup and upgrade instructions](WINDOWS-QUICKSTART.md)**. Server administration is documented in [server/README.md](server/README.md).

## What works in this source version

Students install with **one setup file** (`scripts\Build-Installer.ps1` builds `JOCO-ROBOS-CAD-Setup-<version>.exe`). It installs the add-in and bundled SharpSvn, adds the Visual C++ runtime if needed, registers with SOLIDWORKS, and turns on load-at-startup. Uninstalling never touches `C:\JOCO-ROBOS`. It is not code-signed, so Windows shows "More info → Run anyway" once.

A **JOCO ROBOS CAD task pane** (right side of SOLIDWORKS) shows the robot, ✓ up to date or "N updates available" with who submitted what, the active file's state (🔒 locked by someone, ✎ you are editing, read-only, new), your locked files, and the main buttons. It checks the server every 3 minutes, after each command, and on "Check now". It never changes files.

**Lock on first change, release on close.** The add-in watches team files, however they were opened (Open Robot, File → Open, or as assembly components). Changes in the first 8 seconds after a document loads are ignored, because rebuilds can mark files changed. After that, the first change to a read-only current-season or Library file asks "Lock it for editing now?" (Yes = Edit, keeping the change). If the last status check shows a teammate holds the lock, it warns immediately instead. Closing a file you locked without saving changes releases the lock in the background. Release Edit's rules still apply: only your own lock, only if unchanged on disk.

**Changes made without Edit.** Robot files open read-only, so SOLIDWORKS won't save over them. If a student changes a file first and then clicks **Edit**, the add-in first saves a backup copy of the unsaved work to `C:\JOCO-ROBOS\Set Aside\…`. It then locks the file if it's free and current, keeping the changes. Otherwise it says who is editing and where the backup is. A file changed on disk without Edit (for example, read-only removed in Explorer) is locked automatically by Submit when nobody else holds it and nobody submitted a newer version. Otherwise **Set Aside My Changes** (Tools menu) copies the student's versions to `Set Aside` and restores the team's versions, so Update works again.

**Edit and Release Edit** act on the one component selected in an open assembly (tree or graphics), otherwise on the active document.

The toolbar contains **Open Robot**, **Update**, **Edit**, **Submit**, and **Insert from Library**. The Tools → JOCO ROBOS CAD menu also provides **Sign In**, **Test Connection**, **Release Edit**, and **Choose Robot**.

- **Seasons are automatic.** The add-in reads `https://cad.imdad.stream/catalog.json` and follows the season mentors make active. Each season has its own folder (`C:\JOCO-ROBOS\2028-Robot`); the previous one stays on disk. Choose Robot can pin an older season; archived seasons are read-only. **Open Old Robot** (Tools menu) opens a previous season's master read-only for reference: it downloads that season once and never updates it afterwards. SOLIDWORKS can't hold two files with the same name, so the add-in refuses to open a season while another season's documents are open. Edit is refused on old-season files (Submit only covers the current season and the Library); reuse old parts through the Library.
- **Library.** Update also downloads `C:\JOCO-ROBOS\Library`. With a robot assembly open and locked by Edit, **Insert from Library** copies the chosen part into `90_COTS\<library folder>`. For assemblies, it copies their library parts too and repoints references to the copies. It then inserts the part at the origin. Parts already copied into that robot are reused, never overwritten. Copies are new files, so the next Submit includes them. Library files can be improved with Edit/Submit like robot files; changes reach a robot only when someone inserts the part again. Submit refuses robot files that link directly into the Library.

- Sign In validates your account against the server's catalog and the current season's repository, then saves the login in Windows Credential Manager. SVN password caching and interactive SVN prompts are disabled. Invalid TLS certificates are not accepted.
- Update checks out the complete robot on first use and subsequently uses SVN update. Workspaces are `C:\JOCO-ROBOS\<season>` and `C:\JOCO-ROBOS\Library`. Repository UUIDs from the catalog are checked before use. Existing non-SVN folders are never overwritten.
- Update requires the documents from that folder to be closed. It stops on local changes, unknown files, conflicts, switched files, or unsupported workspace items. It does not merge binary CAD or silently revert files.
- Open Robot updates first, then opens `00_Master\Robot.SLDASM` read-only. If that file does not exist, it opens the only assembly in `00_Master` that no other assembly there references (currently the hexapod's `full assembly.SLDASM`).
- Edit acts on the **active document** in its own window. Open a component separately before locking it. It checks the server revision, requests a non-stealing exclusive lock, and confirms both server ownership and the local lock token. Then it changes SOLIDWORKS to writable using `SetReadOnlyState(false)` without reloading the model.
- Release Edit only unlocks a file with no unsaved or on-disk changes. Modified files and their locks are retained.
- Submit refuses while any SOLIDWORKS document has unsaved changes. It scans the workspace with server lock status and lists **Modified** files you hold locks for, **New** CAD files (including inside new folders; `~$` owner files are ignored), and **Unchanged** locked files to release. Files it cannot submit — changed without a lock, missing/renamed, conflicted — are shown separately and left untouched. After you review and enter a comment, it checks that every submitted CAD's references are inside the workspace and that referenced new files are included. Then it rechecks status, adds new files and folders with `svn:needs-lock` and the binary MIME type, and commits the selection as one revision. It releases only the submitted files' locks, then makes those documents read-only. Unchecked files keep their edits and locks.
- An interrupted Submit is journaled. Next Submit/Update compares the server's latest bytes with your local files: an identical server copy means it landed (reported, then made clean for Update); otherwise edits and locks are kept for a retry. Mixed results stop and ask for a mentor.

Network work runs off the SOLIDWORKS UI thread in an owned modal progress window. A per-user file lock serializes add-in workspace operations across SOLIDWORKS instances. The first upgrade replaces the original two-button prototype tab once; later sessions retain customizations.

## Validation and limits

- The original local toolbar and callbacks were reported working by the user in SOLIDWORKS 2026.
- v0.4 compiles with zero warnings/errors using .NET SDK 8.0.425, .NET Framework 4.8 reference assemblies, SharpSvn 1.14005.390, and SOLIDWORKS 2024 interop 32.1.0 reference DLLs for the Linux compilation check. Those downloaded reference DLLs are not committed or distributed. The Windows build uses your installed SOLIDWORKS 2026 API DLLs.
- 38 policy tests cover update offers (newer-only, numeric versions, safe file names, checksum present), repository names, library copy locations, catalog parsing, path boundaries, traversal, metadata paths, symlinks, extension handling, missing/stale/wrong-user lock tokens, submittable-file filtering, new-folder scheduling, and comment validation. Run `dotnet run --project tests\WorkspacePolicy.Tests -c Release`.
- The server passed actual HTTPS checkout, commit, update, competing lock, lock stealing/breaking denial, and backup restoration tests using the native SVN client.
- Verified on Windows by the user: Sign In, Update, opening the imported hexapod, Edit, and Release Edit.
- **Not yet run on Windows:** Submit, interrupted-Submit recovery, `GetDocumentDependencies2` reference checks, Open Robot's master-assembly fallback, catalog download, Library checkout, and Insert from Library (`ReplaceReferencedDocument`, `AddComponent5`).
- Server side verified: 52 disposable-container checks of the admin page, archive permissions, library upload/promote, and accounts, plus the original integration suite on the Deck.
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
10. Library: with the hexapod's leg assembly locked, Insert from Library a library part. It must appear under `90_COTS`, be inserted, and Submit must list it as New. Inserting it again must reuse the copy.
11. Disconnect the network during Submit. Retry after reconnecting: the result must be either one committed revision or preserved edits and locks, never both or neither.

## Dependencies

The project targets .NET Framework 4.8/x64 and pins [SharpSvn 1.14005.390](https://www.nuget.org/packages/SharpSvn/1.14005.390). NuGet bundles SVN; students will not need the SVN command line. The native library requires the [Microsoft Visual C++ x64 Redistributable](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist). SOLIDWORKS API DLLs come from the installed SOLIDWORKS `api\redist` directory.

API references: [ISwAddin registration](https://help.solidworks.com/2023/English/api/sldworksapiprogguide/Overview/Using_SwAddin_to_Create_a_SolidWorks_Addin.htm), [CommandManager](https://help.solidworks.com/2021/english/api/sldworksapi/SOLIDWORKS.Interop.sldworks~SOLIDWORKS.Interop.sldworks.ICommandManager~CreateCommandGroup2.html), [SetReadOnlyState](https://help.solidworks.com/2026/English/api/sldworksapi/SolidWorks.Interop.sldworks~SolidWorks.Interop.sldworks.IModelDoc2~SetReadOnlyState.html), and [SharpSvn source](https://github.com/AmpScm/SharpSvn).
