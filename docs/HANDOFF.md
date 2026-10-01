# Handoff: where things stand (2026-09-30)

For the next Claude session (cloud) or anyone picking this up.

## Released and live
- **Add-in 1.0.0** is released (tag `v1.0.0`; the same code as 0.15.0, after the release tests passed). Mentors publish it from the mentor page → Add-in → Release to students.
- **Server v25** (deployed 2026-10-01: CAD Hub name and logo, team settings, Diagnostics tab; v24 kept for rollback) runs on the Steam Deck, reached through a Cloudflare Tunnel at `cad.team5919.org` (and the old `cad.imdad.stream`). It has a public front page; everything else needs a sign-in.
- The restore drill (`server/restore-drill.sh`) passed on both the Deck backup and the off-device copy on 2026-09-30.

## New address: cad.team5919.org (0.15.0)
Since 0.15.0 the add-in uses **`https://cad.team5919.org/`**, which is on Cloudflare with a tunnel public hostname pointing to the same Deck service. On a student's next sync, `SvnWorkspace.FollowServerMove` relocates each robot copy from `cad.imdad.stream` (`svn relocate`: same repository, nothing downloaded again). The old address keeps working as a server alias; keep it until every student's add-in reports 0.15.0 or newer on the Accounts tab. The saved-password key in `CredentialStore.cs` keeps its old text on purpose, so saved sign-ins are still found.

Still to do: submit `team5919.org` as Education to the filter categorization services (Fortinet FortiGuard, Palo Alto URL filtering, Cisco Talos, Broadcom/Symantec Site Review, Microsoft "report a site as safe"), then have the JCC student test again.

## Why the new domain
A student at Johnston Community College (JCC) can't connect from the add-in. Server logs (they now show each visitor's real address) prove the add-in's requests never reach us from JCC, while browsers do. JCC's network resets connections to `cad.imdad.stream` by name. The `.stream` ending and a brand-new domain with no category are the likely reasons. Also send JCC IT (JOLT, jolt@mail.johnstoncc.edu, 919-464-2260) a request to allow the address. Don't build anything that hides or disguises traffic to get past a filter. That student also has SOLIDWORKS 2025; the robot is SOLIDWORKS 2026 format, so she needs 2026.

## Any team can run it (1.3.0, branch `any-team`)
- The add-in no longer has 5919's address built in: `TeamServer.cs` asks for it once (Sign In has **Change…**), or reads `HKLM\Software\JOCO ROBOS\CAD\Server`. Computers that already had a sign-in or a robot folder are treated as 5919's and never asked.
- The server takes `JOCO_SERVER_NAME`, `JOCO_TEAM_NAME`, `JOCO_FIRST_SEASON` (see `server/team.env.example`); `server/compose.yaml` runs it with Docker. `server/joco-svn.service` sets 5919's values (deployed with v25).
- SOLIDWORKS' API DLLs left the public repository: CI copies them from the private `Imdad3456/joco-build-files` with the deploy key in secret `SOLIDWORKS_FILES_KEY`. (They're still in old commits.)
- [docs/SETUP.md](SETUP.md) is the guide for other teams. License: MIT.

## Renamed to CAD Hub (1.4.0)
Everything people see says **CAD Hub** (panel, menus, dialogs, installer, SOLIDWORKS add-in list, server pages, docs). Kept on purpose so existing installs keep working and updating: the install folder `C:\Program Files\JOCO ROBOS CAD`, registry `Software\JOCO ROBOS\CAD`, the robot folder `C:\JOCO-ROBOS`, the installer file name `JOCO-ROBOS-CAD-Setup-x.y.z.exe` (older add-ins only accept that name), the saved-password key, the server's sign-in name `AuthName "JOCO ROBOS CAD"` (new add-ins recognize team servers by it), the add-in's COM GUID, the C# namespace, and the repository name.

## After 1.0.0
1.0.x is fixes only. New features go into 1.1 and later. `TESTING.md` stays the regression sheet: rerun the affected sections in the `2099-Robot` test season before releasing anything that touches Edit, Submit, Update or locking.

## What a cloud session can and can't do
- **Can:** edit code, run the add-in unit tests (`dotnet run --project tests/Addin.Tests`, .NET 8), compile the add-in against the net48 reference assemblies (`dotnet build src/JocoRobos.Cad -c Release -p:SolidWorksInteropDir=<repo>/lib/solidworks`), run the server tests if Docker is available (see `.github/workflows/build.yml`), push, and tag. GitHub Actions builds the installer and stages it on the server by itself.
- **Can't:** reach the Steam Deck. It's only reachable over Tailscale from the mentor's Linux PC. **Server changes** (anything under `server/`) have to be deployed from that PC. The procedure: start `joco-svn-backup.service`, copy `server/` to `~/server/joco-cad/source.new`, build `localhost/joco-svn:vNN` with podman, swap the source folders, update `~/.config/systemd/user/joco-svn.service`, then daemon-reload and restart. Bump the tag in `server/joco-svn.service` and `server/README.md` each time. The same goes for `joco.py test-season create/remove`, the restore drill, and reading server logs.

## Rules that stay
- Secrets never go in the repository or in chat: the Onshape key (`~/server/joco-cad/secrets/onshape.env` on the Deck), the tunnel token, passwords, setup codes. Never ask anyone to paste a password.
- The repository stays **public** (students download releases from it).
- The Deck also runs a Discord bot, Lavalink and Pterodactyl: leave them alone.
- Mentors release add-in versions themselves (Release to students); tags only stage them.
- After 1.0: fixes only for anything that could lose or overwrite work, break someone else's robot, block recovery, or make Open → CAD → Submit confusing. Everything else waits for 1.1.
