# JOCO ROBOS CAD — FRC 5919

A SOLIDWORKS 2026 add-in plus a small team server. It lets the whole team work on one robot without overwriting each other's work. Students only see:

**Open Robot → CAD normally → Library when you need a part → Submit**

Everyone keeps a full copy of the robot on their own computer (fast). The team server keeps the history and hands out **exclusive edit locks**: while you're editing a file, nobody else can change it.

## For students

1. **Install:** download the newest `JOCO-ROBOS-CAD-Setup-x.y.z.exe` from [Releases](https://github.com/Imdad3456/joco-robos-cad/releases/latest) and run it. Windows warns about an unknown publisher; click **More info → Run anyway**.
2. **Set up your account:** start SOLIDWORKS, choose a username and password, and click **Send request**. A mentor gives you a code; type it in and click **Finish**. You won't need to type it again.
3. **Daily work:**
   - **Open Robot** gets teammates' latest work and opens the robot. Everything is read-only until you change it.
   - **Just start CADing.** The first change to a part locks it for you (or select a part and click **Edit**). If a teammate is editing it, you're told right away.
   - **Save** normally. New parts go anywhere inside your robot folder.
   - **Submit** when you're done: one window lists your files and fixes any problems. The panel confirms it.
4. **The JOCO ROBOS CAD panel** (right side) shows what's going on and only the button you need right now: Open Robot, Edit, Submit 3, or Close & Update when teammates have new changes. Teammates' changes come in by themselves whenever none of your robot documents are open. The **Library** tab finds any part: the team Library and [FRCDesignLib](https://frcdesign.org/resources/frcdesignlib/) (motors, bearings, tube…) in one search, plus "Import a downloaded CAD file" for vendor downloads.

Everything else is in **Tools → JOCO ROBOS CAD**, in sections: everyday extras (Update, Release Edit, Choose Robot, Open Old Robot, File History), your account (Sign In, Change Password, Test Connection, Copy Diagnostics), and recovery tools (Set Aside My Changes, Restore Deleted Files, Import Outside References, Repair Moved References, Upgrade Robot Files).

**Something wrong?** Click **Copy diagnostics for a mentor** at the bottom of the panel (or Tools → Copy Diagnostics) and paste it to a mentor. It never includes passwords or codes.

## For mentors

Everything is on **https://cad.imdad.stream/admin**:

| Tab | Use it to |
|---|---|
| **Seasons** | Check health (backups, disk), create the next season, choose which season students open, archive old ones, see recent submits and **undo** a bad one |
| **Locks** | See who's editing what and since when; release a lock someone abandoned (after they've confirmed they're done: releasing doesn't keep their unsubmitted changes) |
| **Library** | Add reusable parts; see what's been imported from FRCDesignLib |
| **Accounts** | Give waiting students their code (they asked from SOLIDWORKS with their own username and password), reject requests you don't recognize, reset a forgotten password, see add-in versions |
| **Add-in** | Release a new add-in version to everyone; set the team's **approved SOLIDWORKS version** (a newer SOLIDWORKS can look but not edit or submit, so nobody upgrades the shared files by accident) |

To **release a new add-in version**: bump `<Version>` in `src/JocoRobos.Cad/JocoRobos.Cad.csproj`, commit, then run `git tag v1.0.0 && git push origin main --tags`. When GitHub finishes, click **Release to students**.

## What's in this repository

| Folder | What it is |
|---|---|
| `src/JocoRobos.Cad/` | The SOLIDWORKS add-in (C#, .NET Framework 4.8). `Addin.cs` holds the commands, `SvnWorkspace.cs`/`SvnSubmit.cs` the version-control work (Subversion), `SubmitWindow.cs`/`SubmitCheck.cs` the Submit window and its checks, `StatusPane.cs`/`FrcLibraryPanel.cs` the panel, and `Catalog.cs` the season list. |
| `server/` | Everything that runs on the Steam Deck: the container (`Containerfile`, `svn.conf`, `entrypoint.sh`), the mentor page and APIs (`joco.py`), FRCDesignLib (`frcdesign.py`), lock rules (`pre-*.py`), backups, and service files. See [server/README.md](server/README.md). |
| `installer/` | Inno Setup script for the student installer. |
| `scripts/` | Windows PowerShell: build, build the installer, register a development copy. |
| `tests/Addin.Tests/` | Add-in checks that run without SOLIDWORKS. |
| `server/tests/` | Server checks: the mentor page and APIs, plus lock rules with a real SVN client. |
| `lib/solidworks/` | SOLIDWORKS' redistributable API files, so GitHub can build without SOLIDWORKS. |
| `tools/` | Helpers: `make-icons.py` rebuilds the toolbar icons from `src/JocoRobos.Cad/Icons/source`, and the script that sorted the 2026 robot into folders. |
| `docs/` | [How it works](docs/HOW-IT-WORKS.md), [developer setup](docs/DEVELOPING.md), and [the original design notes](docs/history/ORIGINAL-DESIGN.md). |
| `.github/workflows/build.yml` | On every push: build and test everything and build the installer. On `v*` tags: publish a Release and stage it on the server. |
| [`TESTING.md`](TESTING.md) | The hands-on test sheet for two people. |

The repository is public so students can download releases. It contains **no passwords or keys**: those live only on the Deck and in GitHub's encrypted secrets.

## Status

**1.0 release candidate (0.14).** Feature-frozen: only fixes for problems that could lose or overwrite work, break someone else's robot, block recovery, or make Open → CAD → Submit confusing. Confirmed in SOLIDWORKS 2026: install and automatic updates, sign-in, opening the robot, editing and locking, Submit, FRCDesignLib inserts (with custom lengths), and upgrading an older robot. The rest is being confirmed with [TESTING.md](TESTING.md). 1.0.0 is tagged when that sheet passes.

**Known limits (on purpose, for 1.0):**
- Only SOLIDWORKS files (parts, assemblies, drawings) are shared. Excel design tables, decals, textures and Toolbox data aren't synced; Submit doesn't check them.
- Renaming, moving or deleting team CAD is a mentor task.
- Locking an assembly doesn't lock the parts inside it.
- Locks never expire by themselves; a mentor releases abandoned ones.
- Teammates' changes can't come in while you have unsubmitted work (Submit first).
- No working offline beyond files you already locked; no branches or "experiment copies" (File History can save an older version as a separate copy to look at).
- FRCDesignLib options that take text stay at their defaults (numbers such as custom lengths work).
