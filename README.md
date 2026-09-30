# JOCO ROBOS CAD — FRC 5919

A SOLIDWORKS 2026 add-in that lets the team work on one robot without overwriting each other. Everyone keeps a full local copy of the robot on their SSD. The team server keeps the history and gives out exclusive edit locks. Students only see:

**Open Robot → Edit → CAD normally → Submit**

## For students

1. Download the newest `JOCO-ROBOS-CAD-Setup-x.y.z.exe` from [Releases](https://github.com/Imdad3456/joco-robos-cad/releases/latest) and run it. Windows warns about an unknown publisher (the installer isn't code-signed): click **More info → Run anyway**, then approve the admin prompt.
2. Start SOLIDWORKS. On the **JOCO ROBOS CAD** tab, click **Open Robot** and sign in once with the username and password a mentor gave you. It's saved in Windows Credential Manager.
3. Daily work:
   - **Open Robot**: downloads teammates' changes, then opens the robot. Everything is read-only.
   - **Edit**: select a component in the tree (or open a file), then click **Edit**. You get the lock, and nobody else can change that file until you Submit.
   - **CAD normally** and **Save**. New parts saved inside `C:\JOCO-ROBOS\<season>` are picked up automatically.
   - **Submit**: review the list, type what you changed, done. Your locks are released.
4. The **JOCO ROBOS CAD** panel on the right shows whether you're up to date, who is editing the file you're looking at, and which files you still have locked.

If you change a read-only file without clicking Edit, the add-in asks "Lock it for editing now?". If a teammate is editing it, you're told right away. If a file is stuck (someone else is editing a file you changed), **Tools → JOCO ROBOS CAD → Set Aside My Changes** saves your version in `C:\JOCO-ROBOS\Set Aside` and restores the team's version.

**Library parts.** **Insert from Library** copies a reusable part (motors, bearings, gearboxes…) into your robot's `90_COTS` folder the first time it's used there, then inserts it. From then on, that copy belongs to the robot. Later changes to the Library never replace it, so mates and geometry can't change underneath a design, and inserting the same part again reuses the robot's copy. Library parts themselves can be improved with Edit/Submit; only robots that insert them for the first time afterwards get the new version.

**Other seasons.** When mentors start a new season, Open Robot switches to it automatically. Last year's folder stays on your disk. **Tools → Open Old Robot** opens a previous season read-only for reference. It's never updated, and you can't edit it; to reuse an old part, ask a mentor to add it to the Library.

## For mentors

Everything is on **https://cad.imdad.stream/admin** (sign in with a mentor account):

| Tab | What it does |
|---|---|
| Seasons | Create `YYYY-Robot`, choose the active season, archive old ones read-only, delete an unused empty season, see recent submits |
| Locks | See every lock; release a lock someone abandoned (their unsubmitted copy can then no longer be submitted) |
| Library | Browse, upload parts, or copy a part from any season into the Library |
| Accounts | Add students, reset passwords, make mentors |
| Add-in | Release a new add-in version to students (optionally *required*) |

**Releasing an add-in version:** bump `<Version>` in `src/JocoRobos.Cad/JocoRobos.Cad.csproj`, commit, then:

```bash
git tag v0.7.0
git push origin main --tags
```

GitHub Actions builds the installer, attaches it to a Release, and stages it on the server. Click **Release to students** on the Add-in tab. Each add-in asks its student once, downloads the installer, checks its SHA-256, and installs it when SOLIDWORKS closes. SOLIDWORKS then reopens. **Anyone with a mentor account can push software to every student PC**, so keep mentor accounts few, with strong passwords.

Server operations, backups, and recovery: [server/README.md](server/README.md).

## How it works

- **Server:** the Steam Deck runs Subversion under Apache in a Podman container, published only through Cloudflare Tunnel at `cad.imdad.stream`. It holds one repository per season plus `Library`. Server hooks require the submitting user to own the lock for any existing CAD file. They also force `svn:needs-lock` and a binary MIME type on new CAD, and block lock stealing. The add-in's read-only files are a convenience; the server is what actually enforces ownership.
- **Client:** a C# (.NET Framework 4.8, x64) SOLIDWORKS add-in using SharpSvn. Update refuses to touch local changes, unknown files, or files open in SOLIDWORKS, and it never merges or silently reverts CAD. Edit verifies revision, lock owner, and lock token before making a file writable. Submit rechecks everything after the review dialog and checks that every reference is inside the robot. It then commits one revision and releases only the submitted files' locks. An interrupted Submit is resolved on the next attempt by comparing the server's bytes with yours, so a dropped connection never loses work or submits twice.
- **Backups:** daily verified hotcopies of every repository on the Deck (14 days), and a daily off-device copy on a second computer (90 days).

## Status

This is a **v1.0 candidate**. The design is complete; what remains is testing in real SOLIDWORKS with [TESTING.md](TESTING.md).

- **Confirmed in SOLIDWORKS 2026:** Sign In, Update, Open Robot on the imported hexapod, Edit, Release Edit, the installer, and one automatic update.
- **Built and unit-tested, not yet confirmed in SOLIDWORKS:** Submit and its reference checks, interrupted-Submit recovery, two-user locking through the UI, Insert from Library, the status panel, selected-component Edit, lock-on-first-change and release-on-close, Set Aside, Open Old Robot, and season switching.
- **Server:** 85 automated checks of the admin page and permissions, plus lock/backup integration tests, run on every push and on the Deck.
- **Known limits:** deleting or renaming CAD through Submit isn't supported (a mentor handles it). Locking an assembly doesn't lock its parts. Parts imported from STEP through 3D Interconnect may be missing for teammates until their links are broken (Submit warns).

## Development

| | |
|---|---|
| Build on Windows | `scripts\Build.ps1`, then `scripts\Register-Dev.ps1` as admin ([details](WINDOWS-QUICKSTART.md)) |
| Build the installer | `scripts\Build-Installer.ps1` (needs Inno Setup), or push a tag and let GitHub build it |
| Unit tests | `dotnet run --project tests\WorkspacePolicy.Tests -c Release` |
| Server tests | `.github/workflows/build.yml` runs `server/tests/admin_test.py` and `server/tests/integration.py` in a disposable container |
| CI | Every push builds the server image, runs all tests, and builds the installer; `v*` tags publish a Release and stage it on the server |

Layout: `src/JocoRobos.Cad` (add-in), `server/` (container, hooks, admin page `joco.py`, backups), `installer/` (Inno Setup script), `lib/solidworks` (SOLIDWORKS redistributable API DLLs, so GitHub can build without SOLIDWORKS), `tests/`.

The repository is public so students can download releases. It contains no passwords or tokens: the Cloudflare token, account passwords, and the `github-release` upload password live only on the Deck and in GitHub's encrypted secrets.

Dependencies: [SharpSvn 1.14005.390](https://www.nuget.org/packages/SharpSvn/1.14005.390) (bundles SVN), the Microsoft Visual C++ x64 runtime (bundled in the installer), and SOLIDWORKS 2026's API.
