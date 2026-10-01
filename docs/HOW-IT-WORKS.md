# How JOCO ROBOS CAD works

The details behind the [README](../README.md). Server operations (deploying, backups, recovery, keys) are in [server/README.md](../server/README.md).

## The big picture

```
Student PC (SOLIDWORKS + add-in)            Steam Deck (Podman container)
  C:\JOCO-ROBOS\<season>  ── HTTPS ──►  Cloudflare Tunnel ──► Apache
  C:\JOCO-ROBOS\Library                                         ├─ /svn/<repo>   Subversion: one repo per season + Library
                                                                ├─ /catalog.json which season is active, add-in version
                                                                ├─ /admin        mentor web page (joco.py)
                                                                ├─ /admin/api/*  add-in APIs (FRCDesignLib, check-ins, password)
                                                                └─ /account      first-time password setup (no sign-in)
```

- Every student keeps a **complete local copy** of the robot on their SSD. SOLIDWORKS only ever opens local files, so big assemblies stay fast.
- **Subversion is the source of truth.** It holds the history, exclusive locks, and each file's version. The add-in only translates SOLIDWORKS actions into SVN operations; it never merges CAD or invents its own syncing.
- **The server enforces ownership.** Hooks (`server/pre-commit.py`, `pre-lock.py`, `pre-unlock.py`) refuse changes to an existing CAD file unless the submitting user holds its lock. They force `svn:needs-lock` and a binary MIME type on new CAD files, and block lock stealing. Read-only files on the PC only prevent accidents.

## What each student action does

| Action | Behind the scenes |
|---|---|
| **Open Robot** | Reads the season catalog, runs Update on the season and the Library, then opens the master assembly (a mentor-set path, `00_Master\Robot.SLDASM`, or the only top-level assembly in `00_Master`). |
| **Update** | Automatic: when the status check sees teammates' changes, no robot documents are open, and you have no unsubmitted work, the panel gets them by itself (otherwise it offers **Close & Update**, or says to Submit first). `svn update`, only when nothing from that folder is open. Refuses to touch local changes, unknown files, or conflicts, and never merges or reverts CAD. |
| **Edit** | Checks you have the newest version, takes a non-stealing SVN lock, confirms both the server's lock owner and this PC's lock token, then makes the document writable without reloading it. Works on the component selected in an assembly (lightweight ones are resolved first). |
| **Submit** | Opens one window that first checks everything it can know in advance (`SubmitCheck.cs`): unsaved documents, references outside the robot, new files a checked assembly needs, duplicate names, long paths, files someone else holds, missing files. Each problem has its own fix button (Save these and continue, Lock this file, Import into robot, Include…, Restore) and the window rechecks by itself, keeping the comment. Then the unchanged commit path (`SvnWorkspace.Submit`) rechecks status and locks, adds new files with the lock properties, commits one revision, and releases only the submitted files' locks. A dropped connection is journaled: the next attempt compares the server's bytes with yours, so work is never lost or submitted twice. Success shows in the panel instead of a dialog. |
| **Robot Files** (Robot tab) | A browser of the robot's SOLIDWORKS files only (no .svn, owner files, or other files), listed in the background after each status check and loaded folder by folder. Search matches name and folder. Double-click opens the local copy exactly like File → Open: no lock, Edit, or update. Status marks come from the last status check. No rename, move, delete, or SVN actions. |
| **Library tab** | One search over the team Library (local files) and FRCDesignLib; "Import a downloaded CAD file" copies vendor files in. Parts go into the assembly you're editing, or are opened by themselves. |
| **Team Library insert** | Copies the Library part (and an assembly's parts) into `90_COTS\<library folder>` the first time; later inserts reuse that copy. The robot's copy never changes when the Library does. |
| **FRCDesignLib Insert** | Asks the server whether the team already imported this part + configuration. If yes, it's a Library insert. If not, the server exports it from Onshape once (Parasolid; assemblies flattened to one multi-body part), SOLIDWORKS saves it as a native `.SLDPRT` in `Library\FRCDesignLib\<category>`, it's submitted to the Library, then inserted. The first request reserves the item for that student **on that computer** (a token per claim), so there are no duplicates, even from the same account on two PCs. It heals itself: if the file reached the Library but the last step failed, the next request just uses it; if it didn't, leftovers are cleared. Configuration choices follow FRCDesignApp's own rules (conditions, ranges, per-option visibility). Number options such as a shaft's or spacer's length are typed in inches, checked against FRCDesignLib's limits, and sent to Onshape in meters; each custom size becomes its own Library part, like "Hex Shaft (VEX) (Length 2.5in)". The server counts real Onshape API calls and stops new imports before the yearly allowance runs out; parts already in the Library keep working. |

**Limits on purpose:** new file paths longer than 240 characters are refused (Windows and SOLIDWORKS get unreliable near 260). Only SOLIDWORKS files (`.SLDPRT`, `.SLDASM`, `.SLDDRW`) are managed. Design-table spreadsheets, decals, Toolbox, and similar non-CAD dependencies aren't synced yet; report any you run into.

**Safety nets:**
- The first change to a free read-only team part locks it automatically (the same as Edit); a file someone else holds says so instead.
- Saving a **new** file with the same name as a team file warns right away (Ctrl+S on a read-only file opens Save As, which is how this usually happens).
- Submit's Save button saves only writable robot and Library documents that have unsaved changes, never Save All.
- Closing a locked, unchanged file releases the lock.
- The panel warns about unsaved or unsubmitted work, and SOLIDWORKS asks once on exit.
- **Set Aside My Changes** keeps your version when someone else holds the file.
- **Restore Deleted Files** brings back accidentally deleted team files.
- **Import Outside References** and **Insert External Part** copy downloaded or outside files into the robot.
- **Repair Moved References** fixes links after a folder reorganization.

## Seasons

- The Deck holds one repository per season (`2026-Robot`, `2027-Robot`…) plus `Library`.
- Mentors choose the active season on the web page, and the add-in follows it automatically. A student with unfinished work in the old season stays there until it's submitted or set aside.
- Archived seasons are read-only (SVN access rules). **Open Old Robot** opens any season read-only for reference.
- SOLIDWORKS can't hold two files with the same name, so the add-in refuses to open two seasons at once.
- **Upgrade Robot Files** converts every file saved in an older SOLIDWORKS to the current format in one submit. Until then, opening a part marks it changed, and every save offers to save read-only team parts somewhere else. It locks the whole robot first and refuses if anyone is editing.
- Existing CAD is imported with `joco.py import-season` (see server README). The 2026 robot was reorganized with `tools/reorganize-2026.py` and repaired with **Repair Moved References** first.

## Accounts and security

- **Passwords are the student's own.** A new student chooses a username and password in the add-in and clicks **Send request**. The Accounts tab lists them with a one-time code (7 days); a mentor hands it over in person, and the student types it in to activate. Activation needs the code *and* the password they asked with, so nobody can take over a name someone else requested; mentors reject requests they don't recognize. Mentors can still make codes ahead of time (with or without a name, or for a new mentor). The password is saved only in Windows Credential Manager. **New setup code** disables a forgotten password immediately.
- **Locks and tokens are per computer.** Taking a lock on another PC with the same account shows "You locked this from another computer".
- **Shared PCs:** `C:\JOCO-ROBOS` belongs to its first Windows user; other Windows users get a private `%USERPROFILE%\JOCO-ROBOS`. Switching CAD accounts is refused while there's unsubmitted work.
- **Secrets never enter the repository.** The Cloudflare token, the Onshape API key, and passwords live only on the Deck; the release upload password lives only in a GitHub secret. The `github-release` account can only stage installers and is denied all SVN access.
- **Undo a submit** (web page) makes a new revision restoring the previous files, so history is kept. It's refused if a later submit changed those files or someone holds their lock. It's the only way around the lock hook, and only for mentors.

## Add-in updates and releases

- Push a `v*` tag, and GitHub Actions (`.github/workflows/build.yml`) builds the installer, attaches it to a Release, and stages it on the server with its SHA-256.
- A mentor clicks **Release to students**. Each add-in offers the update once and checks the SHA-256. The installer waits for SOLIDWORKS to close, installs silently (logging to `%LOCALAPPDATA%\JocoRobos.Cad\updates`), and reopens SOLIDWORKS.
- A *required* update blocks **new** edits and inserts until installed. Submit, Set Aside, and Release Edit always work, so nobody's unfinished work is trapped behind an update.
- Every add-in reports its version, so the Accounts tab shows who has what.

## SOLIDWORKS version

Mentors set the team's **approved SOLIDWORKS version** on the Add-in tab. A computer with a *newer* SOLIDWORKS can open and look at the robot, but Edit, inserts, imports, Submit and Upgrade Robot Files refuse, because files it saved couldn't be opened by everyone else; the panel says why. An older SOLIDWORKS is told to update. Every add-in reports its version, shown on the Accounts tab (in red when it doesn't match). The team upgrades together: everyone installs the new SOLIDWORKS, a mentor changes the approved version, then runs Upgrade Robot Files once.

## Support

- **Copy Diagnostics** (panel link, or Tools menu) copies a report to the clipboard and saves it as a text file: add-in, SOLIDWORKS and Windows versions, account name, workspace folder, server reachability and timing, per-robot state (versions here and on the server, changes waiting, locks, interrupted Submit), open windows, and the latest errors and slow operations. Passwords, codes, tokens and sign-in headers are never read or are scrubbed (`Diagnostics.Sanitize`, unit-tested).
- **Errors and slow operations** go to `%LOCALAPPDATA%\JocoRobos.Cad\errors.log`: any window-level error (instead of crashing SOLIDWORKS), progress windows over 5 s, reference reads over 1.5 s, Submit checks over 3 s, and status checks over 20 s.
- **File History** lists recent submits of a team file (who, when, comment) and can save an older version as a separate copy outside the robot folder. It never changes the live robot.

## Interrupted Submit

Before committing, Submit writes a small journal of what it's sending. If the connection drops, SOLIDWORKS closes, or the answer never arrives, the panel says "An earlier Submit was interrupted" and the next Submit settles it: for each file it compares the server's newest version (author, revision, and content hash) with the file on disk. Files the server already has exactly are settled with nothing sent twice; files that never arrived keep their edits and locks and go with this Submit. A commit is all-or-nothing, so a mix only means some files were saved again afterwards: the ones the server has are settled, the newer edits are kept, and the student is told which. When the robot and the Library are submitted together and only one gets through, the window says which one and the other stays ready to Submit.

## Backups

- **On the Deck:** a daily verified `svnadmin hotcopy` of every repository plus the configuration, kept 14 days.
- **Off the Deck:** a second computer pulls each completed backup daily over SSH and keeps 90 days (`server/offsite-pull.sh`). The Deck can't delete those copies.
- **Health panel:** the Seasons tab shows both backup times, the last restore drill, and free disk space.
- **Restore drill:** `server/restore-drill.sh <backup folder> --report` restores a backup into a throwaway server (never the live one), verifies every repository, reads every file back, checks history, locks, accounts and settings, and records the result on the Health panel. Run it on the Deck against its newest backup and on the off-device computer against its newest copy, a couple of times a season.

## Testing

| What | How |
|---|---|
| Add-in logic without SOLIDWORKS | `tests/Addin.Tests` (paths, locks, catalog, updates, library naming, Submit checks) |
| Mentor page, APIs, permissions, undo, accounts, FRCDesignLib (fake catalog) | `server/tests/admin_test.py` against a disposable container |
| Lock hooks and backup restore with a real SVN client | `server/tests/integration.py` |
| SOLIDWORKS itself | by hand, with [TESTING.md](../TESTING.md) |

GitHub Actions runs the first three and builds the installer on every push.
