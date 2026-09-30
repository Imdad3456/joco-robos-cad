# JOCO ROBOS CAD — FRC 5919

A SOLIDWORKS 2026 add-in plus a small team server. It lets the whole team work on one robot without overwriting each other's work. Students only see:

**Open Robot → Edit → CAD normally → Submit**

Everyone keeps a full copy of the robot on their own computer (fast). The team server keeps the history and hands out **exclusive edit locks**: while you're editing a file, nobody else can change it.

## For students

1. **Install:** download the newest `JOCO-ROBOS-CAD-Setup-x.y.z.exe` from [Releases](https://github.com/Imdad3456/joco-robos-cad/releases/latest) and run it. Windows warns about an unknown publisher; click **More info → Run anyway**.
2. **Set up your account:** start SOLIDWORKS. Enter your username and the **setup code** from a mentor, then choose your own password. You won't need to type it again.
3. **Daily work:**
   - **Open Robot** gets teammates' latest work and opens the robot. Everything is read-only.
   - **Edit**: select the part or assembly you're working on and click Edit. It's yours until you Submit.
   - **CAD normally**, and **Save**. New parts go anywhere inside your robot folder.
   - **Submit**: check the list, say what you changed, done.
4. **The JOCO ROBOS CAD panel** (right side): the **Robot** tab shows whether you're up to date, who's editing what, and your unsubmitted work. **Release Edit** gives back a file you didn't change, and **Release my unchanged files** (under your locked files) gives back all of them at once. The **Library** tab searches the team Library and [FRCDesignLib](https://frcdesign.org/resources/frcdesignlib/) (motors, bearings, gearboxes…) and inserts parts with one click.

Less common things live in **Tools → JOCO ROBOS CAD**: Change Password, Release Edit, Set Aside My Changes, Restore Deleted Files, Insert External Part, Import Outside References, Open Old Robot, Choose Robot.

## For mentors

Everything is on **https://cad.imdad.stream/admin**:

| Tab | Use it to |
|---|---|
| **Seasons** | Check health (backups, disk), create the next season, choose which season students open, archive old ones, see recent submits and **undo** a bad one |
| **Locks** | Release a lock someone abandoned |
| **Library** | Add reusable parts; see what's been imported from FRCDesignLib |
| **Accounts** | Add students (you get a setup code; they choose their password), reset a forgotten password, see add-in versions |
| **Add-in** | Release a new add-in version to everyone |

To **release a new add-in version**: bump `<Version>` in `src/JocoRobos.Cad/JocoRobos.Cad.csproj`, commit, then run `git tag v1.0.0 && git push origin main --tags`. When GitHub finishes, click **Release to students**.

## What's in this repository

| Folder | What it is |
|---|---|
| `src/JocoRobos.Cad/` | The SOLIDWORKS add-in (C#, .NET Framework 4.8). `Addin.cs` holds the commands, `SvnWorkspace.cs`/`SvnSubmit.cs` the SVN work, `StatusPane.cs`/`FrcLibraryPanel.cs` the panel, and `Catalog.cs` the season list. |
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

**v1.0 candidate.** Sign-in, updating, opening the robot, locking, the installer, and automatic updates are confirmed in SOLIDWORKS 2026. The rest is built and passes automated checks, and is being confirmed by hand with [TESTING.md](TESTING.md).

Known limits: renaming or deleting team CAD is a mentor task; locking an assembly doesn't lock the parts inside it; FRCDesignLib options that take a number or text stay at their defaults for now.
