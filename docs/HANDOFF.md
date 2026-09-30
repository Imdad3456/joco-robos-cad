# Handoff: where things stand (2026-09-30)

For the next Claude session (cloud) or anyone picking this up.

## Released and live
- **Add-in 0.14.4** is the newest release on `main` (tag `v0.14.4`). Mentors publish it from the mentor page → Add-in → Release to students.
- **Server v24** runs on the Steam Deck, reached through a Cloudflare Tunnel at `cad.imdad.stream`. It has a public front page; everything else needs a sign-in. It already answers to `cad.team5919.org` (ServerAlias) once DNS and the tunnel route exist.
- The restore drill (`server/restore-drill.sh`) passed on both the Deck backup and the off-device copy on 2026-09-30.

## This branch: `new-domain` (NOT released)
The add-in moves to **`https://cad.team5919.org/`**. On a student's next sync, `SvnWorkspace.FollowServerMove` relocates each robot copy from `cad.imdad.stream` to the new address (`svn relocate`; same repository, nothing downloaded again). The old address keeps working.

**Don't merge or tag this until `https://cad.team5919.org/` shows the front page**, or every add-in breaks. Steps:
1. Mentor (Cloudflare dashboard): add `team5919.org` to Cloudflare (nameservers at the registrar if it was bought elsewhere). Then Zero Trust → Networks → Tunnels → the JOCO tunnel → Public Hostname → add `cad` + `team5919.org` with the **same Service** as the existing `cad.imdad.stream` entry.
2. Check: `curl -s -o /dev/null -w "%{http_code}" https://cad.team5919.org/` gives `200`, and `/svn/2026-Robot/` gives `401`.
3. On this branch: bump `<Version>` to 0.15.0, switch `.github/workflows/build.yml` and `scripts/Build-Installer.ps1` URLs to `cad.team5919.org`, update README/docs URLs, merge to `main`, tag `v0.15.0`. CI stages it; a mentor releases it.
4. Give the mentor the categorization forms to submit `team5919.org` as Education: Fortinet FortiGuard, Palo Alto URL filtering, Cisco Talos, Broadcom/Symantec Site Review, and Microsoft's "report a site as safe".

## Why the new domain
A student at Johnston Community College (JCC) can't connect from the add-in. Server logs (they now show each visitor's real address) prove the add-in's requests never reach us from JCC, while browsers do. JCC's network resets connections to `cad.imdad.stream` by name. The `.stream` ending and a brand-new domain with no category are the likely reasons. Also send JCC IT (JOLT, jolt@mail.johnstoncc.edu, 919-464-2260) a request to allow the address. Don't build anything that hides or disguises traffic to get past a filter. That student also has SOLIDWORKS 2025; the robot is SOLIDWORKS 2026 format, so she needs 2026.

## Road to 1.0.0
Run `TESTING.md` (two PCs in the disposable `2099-Robot` season, an uncoached student, a one-week pilot with features frozen). Fix only failures. Then bump to 1.0.0, change the README status, and tag. Mentors should also set **Team SOLIDWORKS version = 2026** on the Add-in tab.

## What a cloud session can and can't do
- **Can:** edit code, run the add-in unit tests (`dotnet run --project tests/Addin.Tests`, .NET 8), compile the add-in against the net48 reference assemblies (`dotnet build src/JocoRobos.Cad -c Release -p:SolidWorksInteropDir=<repo>/lib/solidworks`), run the server tests if Docker is available (see `.github/workflows/build.yml`), push, and tag. GitHub Actions builds the installer and stages it on the server by itself.
- **Can't:** reach the Steam Deck. It's only reachable over Tailscale from the mentor's Linux PC. **Server changes** (anything under `server/`) have to be deployed from that PC. The procedure: start `joco-svn-backup.service`, copy `server/` to `~/server/joco-cad/source.new`, build `localhost/joco-svn:vNN` with podman, swap the source folders, update `~/.config/systemd/user/joco-svn.service`, then daemon-reload and restart. Bump the tag in `server/joco-svn.service` and `server/README.md` each time. The same goes for `joco.py test-season create/remove`, the restore drill, and reading server logs.

## Rules that stay
- Secrets never go in the repository or in chat: the Onshape key (`~/server/joco-cad/secrets/onshape.env` on the Deck), the tunnel token, passwords, setup codes. Never ask anyone to paste a password.
- The repository stays **public** (students download releases from it).
- The Deck also runs a Discord bot, Lavalink and Pterodactyl: leave them alone.
- Mentors release add-in versions themselves (Release to students); tags only stage them.
- After 1.0: fixes only for anything that could lose or overwrite work, break someone else's robot, block recovery, or make Open → CAD → Submit confusing. Everything else waits for 1.1.
