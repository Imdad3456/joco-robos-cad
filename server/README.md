# Steam Deck SVN service

The server hosts one SVN repository per robot season (`2027-Robot`, `2028-Robot`, …) plus a shared `Library` of reusable parts. Mentors manage it from **https://cad.imdad.stream/admin**. Students never use the web page; the add-in reads `/catalog.json` to find the active season and the library.

## Mentor web page

Sign in with your CAD account; only accounts marked as mentor get past the first page. Apache checks the password and forwards `/admin` to `joco.py serve`. That service listens only inside the container and trusts only the username Apache sets. Forms require same-origin requests plus a per-user token.

- **Seasons:** create `YYYY-Robot` with the standard folders, lock hooks and backups; choose the active season students open; archive old seasons as read-only (SVN authz), or unarchive them. A season can't be archived while it's active or has locks. An inactive season nobody has submitted to can be deleted; it's moved to a hidden `.deleted-…` folder on the Deck, not erased.
- **Locks:** list every lock and release abandoned ones (`svnadmin rmlocks`). The previous owner can no longer submit their copy of that file.
- **Library:** browse, upload new parts (up to 100 MB per request, the Cloudflare limit), or copy a CAD file from any season into the library. Changes to existing library parts go through Edit/Submit like robot files; students can also add parts that way.
- **Accounts:** adding a student (or "New setup code") creates a one-time setup code, valid 7 days, shown only on this page (never in a URL). Until it's used, the account has a random password nobody knows. The student enters username + code in the add-in and chooses their own password through `POST /account/setup`. That's the only address reachable without signing in; it's rate-limited per address, and 5 wrong codes delete the code. Students change their own password with `POST /admin/api/password` from the add-in. Mentors can also make or remove mentors and delete accounts that hold no locks.

`state.json` in the config directory is the source of truth for the active season, archived seasons, and mentors. `authz` and `public/catalog.json` are generated from it. From SSH: `podman exec joco-svn runuser -u www-data -- python3 /opt/joco/joco.py mentor add USER`.

## Deployment

The Deck runs SteamOS with rootless Podman as `deck`. Files are under `~/server/joco-cad`, outside the immutable OS. `joco-svn.service` starts with the user manager; lingering was already enabled. Apache/Subversion listens only at `127.0.0.1:8091`, with repositories at `http://127.0.0.1:8091/svn/<name>` on the Deck. That HTTP address is loopback-only; everyone else uses `https://cad.imdad.stream` through the tunnel.

The container uses Debian Bookworm packages and persistent bind mounts for repositories and authentication. Its startup creates the canonical subsystem folders only if the repository is absent. It installs hooks on restart. There is no default account/password, anonymous access is denied, and temporary integration-test accounts are removed after testing.

Accounts are normally managed from the web page. For the very first mentor, or if the page is unavailable, run from a terminal with SSH access to the Deck:

```sh
ssh -t deck@100.97.7.84 'podman exec -it joco-svn htpasswd -B /etc/joco/users imdad'
```

Enter the new CAD password at the prompt; do not put it in a command, GitHub, or chat. Use unique usernames for each student. All authenticated users can read every repository and write to non-archived ones. Passwords are bcrypt hashes; configuration stays on the Deck, not in Git.

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

The public repository URL is **https://cad.imdad.stream/svn/2027-Robot**. The `joco-cad` tunnel routes `cad.imdad.stream` to `http://127.0.0.1:8091` on the Deck, retaining the request path. `joco-cad-tunnel.service` runs the official cloudflared 2026.9.3 container, pinned by image digest, with host networking to reach the loopback-only origin. The token is at `~/server/joco-cad/tunnel/token` (mode 600), mounted read-only. It is not committed to GitHub. Connector metrics are loopback-only at port 2091.

SVN uses its own per-user authentication; no browser-only Cloudflare Access login is placed in front of it. Anonymous requests receive HTTP 401. To repeat the integration suite over the actual public HTTPS route:

```sh
podman exec -i -e JOCO_TEST_BASE_URL=https://cad.imdad.stream/svn joco-svn python3 - < ~/server/joco-cad/source/tests/integration.py
```

Cloudflare may reject generic Python HTTP user agents; the anonymous probe identifies itself as an SVN integration test, and actual operations use the real SVN client. No firewall or bot protections were disabled. Cloudflare's free plan limits a single request to 100 MB, which applies to the admin page's uploads. SVN commits send each file separately, but very large single CAD files should be tested (TESTING.md step 30). See [Cloudflare's tunnel setup documentation](https://developers.cloudflare.com/tunnel/get-started/).

The token is not included in the daily repository backup. If rebuilding the Deck, obtain a fresh connector token from the tunnel dashboard, store it with restrictive permissions, install this service file in the user systemd directory, and enable it. Rotating the token requires updating this file and restarting the connector.

## Operations

```sh
systemctl --user status joco-svn.service
podman logs --tail 30 joco-svn
systemctl --user list-timers joco-svn-backup.timer
```

To upgrade, take a backup, copy this directory to `~/server/joco-cad/source`, build a new tag (`podman build -f Containerfile -t localhost/joco-svn:vN ~/server/joco-cad/source`), update the tag in `joco-svn.service`, then `daemon-reload` and restart. Keep the previous tag for rollback. Do not restart during a commit. Current tag: `v18`; earlier tags (`v17` … `initial`) are rollback images.
