# Steam Deck SVN service

> **This page is Team 5919's own server** (a Steam Deck with Podman, behind a Cloudflare Tunnel). Setting one up for another team: [docs/SETUP.md](../docs/SETUP.md).

The server hosts one SVN repository per robot season (`2027-Robot`, `2028-Robot`, …) plus a shared `Library` of reusable parts. Mentors manage it from **https://cad.team5919.org/admin**. Students never use the web page; the add-in reads `/catalog.json` to find the active season and the library.

## Mentor web page

**Sign in with Discord**; only accounts marked as mentor get past the first page. `joco.py serve` listens only inside the container. It trusts the username Apache sets after checking a username and password (the add-in's key, or a password account), and otherwise its own signed Discord session cookie (7 days, `/admin` only, never accepted by `/admin/api`). Forms require same-origin requests plus a per-user token.

- **Seasons:** create `YYYY-Robot` with the standard folders, lock hooks and backups; choose the active season students open; archive old seasons as read-only (SVN authz), or unarchive them. A season can't be archived while it's active or has locks. An inactive season nobody has submitted to can be deleted; it's moved to a hidden `.deleted-…` folder on the Deck, not erased.
- **Locks:** list every lock and release abandoned ones (`svnadmin rmlocks`). The previous owner can no longer submit their copy of that file.
- **Library:** browse, upload new parts (up to 100 MB per request, the Cloudflare limit), or copy a CAD file from any season into the library. Changes to existing library parts go through Edit/Submit like robot files; students can also add parts that way.
- **Accounts:** everyone in the team's Discord server signs in with Discord (`discord_auth.py`, see below); there's nothing to create. Mentors make or remove mentors, **Sign out everywhere** (changes that person's key on every computer and ends their mentor-page session), and delete accounts that hold no locks. Password accounts (not Discord) are listed separately.

`state.json` in the config directory is the source of truth for the active season, archived seasons, and mentors. `authz` and `public/catalog.json` are generated from it. From SSH: `podman exec joco-svn runuser -u www-data -- python3 /opt/joco/joco.py mentor add USER`.

## Discord sign-in

Students and mentors sign in with Discord; their CAD username is their Discord username (made once, at the first sign-in; a later
Discord rename doesn't rename it). The add-in opens `/account/discord/start`, Discord sends the browser back to
`/account/discord/callback`, and the add-in collects a key with `POST /account/discord/poll`, using a secret that never went through
the browser. The key is the SVN password (bcrypt in `/etc/joco/users`), derived from `config/discord-secret`, the Discord id and a
generation number, so every computer gets the same one until **Sign out everywhere** bumps the generation. `config/discord.json`
(600) holds each Discord id's username, generation and refresh token. Membership is checked at each sign-in and daily; anyone who
left the server (or disconnected CAD Hub in Discord) is signed out everywhere. An outage never removes anyone.

Existing password accounts with a student's Discord username are taken over at that student's first Discord sign-in (the old password
stops working; history and locks stay). A mentor's account is only taken over by a Discord id in `DISCORD_MENTOR_IDS`; anyone else
with that name gets `name-2`. `github-release` is never touched.

Configuration (environment, from `~/server/joco-cad/secrets/discord.env`, mode 600, passed with `--env-file`): `DISCORD_CLIENT_ID`,
`DISCORD_CLIENT_SECRET`, `DISCORD_GUILD_ID`, `DISCORD_GUILD_NAME`, `DISCORD_MENTOR_IDS`. Team 5919 shares the quote book's Discord
application, with `https://cad.team5919.org/account/discord/callback` added to its redirects. Without the file the service starts but
nobody new can sign in (the mentor page says so).

Password accounts, for special cases only (`github-release`, or a spare account if Discord is down):

```sh
podman exec -it joco-svn runuser -u www-data -- python3 /opt/joco/joco.py password-account NAME
podman exec joco-svn runuser -u www-data -- python3 /opt/joco/joco.py remove-password-accounts [KEEP ...]
```

The second removes every password account except `github-release`, Discord accounts, and the names given (accounts holding locks are
kept and listed). Run it once everyone has signed in with Discord; until then, older sign-ins keep working.

## Deployment

The Deck runs SteamOS with rootless Podman as `deck`. Files are under `~/server/joco-cad`, outside the immutable OS. `joco-svn.service` starts with the user manager; lingering was already enabled. Apache/Subversion listens only at `127.0.0.1:8091`, with repositories at `http://127.0.0.1:8091/svn/<name>` on the Deck. That HTTP address is loopback-only; everyone else uses `https://cad.team5919.org` through the tunnel.

The container uses Debian Bookworm packages and persistent bind mounts for repositories and authentication. Its startup creates the canonical subsystem folders only if the repository is absent. It installs hooks on restart. There is no default account/password, anonymous access is denied, and temporary integration-test accounts are removed after testing.

Accounts come from Discord sign-in (above). If Discord is unavailable, a mentor can make a password account from SSH with `joco.py password-account NAME` (it asks for the password; never put one in a command, GitHub, or chat). All authenticated users can read every repository and write to non-archived ones. Keys and passwords are stored as bcrypt hashes; configuration stays on the Deck, not in Git.

## FRCDesignLib adapter

`frcdesign.py` serves `/admin/api/frcdesign/*` to the add-in (any signed-in student; requires the add-in's `X-Joco-Client` header). It reads the public FRCDesignLib catalog from `app.frcdesign.org` (cached, refreshed hourly when its version changes; the last good copy is used if the site is down) and proxies thumbnails. It exports geometry through Onshape with **signed requests**: Part Studios as Parasolid in one call, and assemblies as a flattened multi-body Parasolid (one translation plus a few polls). Exports are cached under `data/.frcdesign`. Imports are recorded in `config/frcdesign.json` (fingerprint → Library file, who, when, source IDs). Each part/configuration is reserved for the first student, so two students can't create duplicates. Limits: 40 exports per day for the team and 12 per student (`JOCO_FRC_DAILY_EXPORTS`, `JOCO_FRC_USER_DAILY_EXPORTS`), because Onshape allows 2,500 API calls a year on Free/EDU plans.

The Onshape API key (read-documents scope only) is in `~/server/joco-cad/secrets/onshape.env` (mode 600) and reaches the container only through `--env-file`. It's never returned, logged, or backed up; like the tunnel token, recreate it after rebuilding the Deck. To replace it:

```sh
ssh -t deck@100.97.7.84 'umask 077; read -rp "Onshape access key: " a; read -rsp "Onshape secret key (hidden): " s; echo; a=$(printf %s "$a" | tr -d "[:space:]"); s=$(printf %s "$s" | tr -d "[:space:]"); printf "ONSHAPE_ACCESS_KEY=%s\nONSHAPE_SECRET_KEY=%s\n" "$a" "$s" > ~/server/joco-cad/secrets/onshape.env; echo "saved: ${#a} / ${#s} characters"; systemctl --user restart joco-svn.service'
```

The container won't start without that file; create an empty one to run without FRCDesignLib export.

## Lock enforcement

The pre-commit hook requires a lock owned by the submitting user for modifications, property changes, deletion, or replacement of existing `.SLDPRT`, `.SLDASM`, and `.SLDDRW` files (case-insensitive). SVN additionally checks the client's lock token. Newly committed CAD must have `svn:needs-lock` and `svn:mime-type=application/octet-stream`.

Pre-lock and pre-unlock hooks prevent force-stealing and force-breaking locks. Directory deletion/replacement is blocked so it can't bypass per-file ownership; deleting or renaming CAD is a mentor task done with the SVN command line on the Deck. A lock is per file, not per assembly tree. These hooks are the real enforcement; the add-in's read-only files only prevent accidents.

## Validation

Discord sign-in: `python3 server/tests/discord_test.py` (no network). Through Apache, in a disposable container on 127.0.0.1:18191
named `joco-discord-test` with users `mentor1`/`mentorpass123` (mentor) and `sarah`/`sarahpass123` and fake `DISCORD_*` settings
(`DISCORD_MENTOR_IDS=900`): `python3 server/tests/discord_http_test.py`.

Run the integration test only during setup/maintenance, with no concurrent account administration. It temporarily creates two users in the password file and a disposable repository, then restores the password file and removes the test repository in `finally`:

```sh
podman exec -i joco-svn python3 - < ~/server/joco-cad/source/tests/integration.py
```

The tests exercise real Apache HTTP checkout, property requirements, read-only working copies, unlocked change rejection, competing locks, force-lock/unlock rejection, lock release on commit, updates, deletion restrictions, and hotcopy restoration. Fixture files only use CAD extensions; they are not SOLIDWORKS documents. These tests do not establish Windows/SharpSvn compatibility or tunnel compatibility.

## Backups

`joco-svn-backup.timer` creates a verified hotcopy of every repository (all seasons and the Library) plus configuration and deployment source each day, retaining completed backups for 14 days under `~/server/joco-cad/backups`. Failed backups stay for inspection. The initial backup is run during deployment. These copies are on the same SSD: an off-device destination must be configured before storing irreplaceable CAD.

```sh
systemctl --user start joco-svn-backup.service
journalctl --user -u joco-svn-backup.service -n 30
```

**Off-device copy.** A second computer pulls each completed backup over SSH with `offsite-pull.sh` (`joco-offsite.service`/`.timer`, daily at 04:30 and at next boot if missed). It keeps 90 days and never deletes the newest copy. The Deck can't delete those copies. It currently runs on Imdad's Linux PC into `~/Backups/joco-cad`. To set it up on another machine with SSH access to the Deck: copy the script to `~/.local/share/joco-cad/`, copy the unit files to `~/.config/systemd/user/`, then run `systemctl --user enable --now joco-offsite.timer`.

For disaster recovery, stop the SVN service, preserve the damaged data separately, copy verified backup repositories into `data/<name>`, restore configuration if needed (`podman unshare cp -a backup/config/. config/`), and restart; startup fixes file ownership. Do not overlay repository database files while the service is running. Locks and existing working copies require review after recovery; do not silently break locks to repair them.

## Cloudflare Tunnel

The public repository URL is **https://cad.team5919.org/svn/2027-Robot**. The `joco-cad` tunnel routes `cad.team5919.org` to `http://127.0.0.1:8091` on the Deck, retaining the request path. `joco-cad-tunnel.service` runs the official cloudflared 2026.9.3 container, pinned by image digest, with host networking to reach the loopback-only origin. The token is at `~/server/joco-cad/tunnel/token` (mode 600), mounted read-only. It is not committed to GitHub. Connector metrics are loopback-only at port 2091.

SVN uses its own per-user authentication; no browser-only Cloudflare Access login is placed in front of it. Anonymous requests receive HTTP 401. To repeat the integration suite over the actual public HTTPS route:

```sh
podman exec -i -e JOCO_TEST_BASE_URL=https://cad.team5919.org/svn joco-svn python3 - < ~/server/joco-cad/source/tests/integration.py
```

Cloudflare may reject generic Python HTTP user agents; the anonymous probe identifies itself as an SVN integration test, and actual operations use the real SVN client. No firewall or bot protections were disabled. Cloudflare's free plan limits a single request to 100 MB, which applies to the admin page's uploads. SVN commits send each file separately, but very large single CAD files should be tested (TESTING.md step 30). See [Cloudflare's tunnel setup documentation](https://developers.cloudflare.com/tunnel/get-started/).

The token is not included in the daily repository backup. If rebuilding the Deck, obtain a fresh connector token from the tunnel dashboard, store it with restrictive permissions, install this service file in the user systemd directory, and enable it. Rotating the token requires updating this file and restarting the connector.

## Operations

```sh
systemctl --user status joco-svn.service
podman logs --tail 30 joco-svn
systemctl --user list-timers joco-svn-backup.timer
```

To upgrade, take a backup, copy this directory to `~/server/joco-cad/source`, build a new tag (`podman build -f Containerfile -t localhost/joco-svn:vN ~/server/joco-cad/source`), update the tag in `joco-svn.service`, then `daemon-reload` and restart. Keep the previous tag for rollback. Do not restart during a commit. Current tag: `v29`; earlier tags (`v28` … `initial`) are rollback images.

## Restore drill (prove the backups work)

```sh
# On the Deck, newest Deck backup:
sh ~/server/joco-cad/source/restore-drill.sh "$(ls -1d ~/server/joco-cad/backups/2*/ | tail -1)" --report
# On the off-device computer, newest pulled copy:
sh server/restore-drill.sh "$(ls -1d ~/Backups/joco-cad/2*/ | tail -1)" --report
```

It copies the backup to a temporary folder, starts a throwaway server from it on a spare local port, verifies every repository, reads every file back, checks history, locks, accounts and settings, then deletes the copy. The live server is never touched. `--report` shows the result on the mentor page's Health panel.

## Test season (release testing without touching the real robot)

```sh
podman exec -u www-data joco-svn python3 /opt/joco/joco.py test-season create 2099-Robot 2026-Robot
podman exec -u www-data joco-svn python3 /opt/joco/joco.py test-season remove 2099-Robot
```

`create` makes a full copy of a season (files and history) with its own identity and no locks; students pick it with Tools → Choose Robot. `remove` only works on seasons made this way, and keeps the folder as `.deleted-…` on the server.
