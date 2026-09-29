# Steam Deck SVN service

This is the server milestone, separate from the Windows add-in. The add-in still has only its two local prototype commands. No real robot files have been uploaded.

## Deployment

The Deck runs SteamOS with rootless Podman as `deck`. Files are under `~/server/joco-cad`, outside the immutable OS. `joco-svn.service` starts with the user manager; lingering was already enabled. Apache/Subversion listens only at `127.0.0.1:8091`, with repository URL `http://127.0.0.1:8091/svn/2027-Robot` on the Deck. This HTTP URL is private loopback only; students must use the eventual HTTPS endpoint.

The container uses Debian Bookworm packages and persistent bind mounts for repositories and authentication. Its startup creates the canonical subsystem folders only if the repository is absent. It installs hooks on restart. There is no default account/password, anonymous access is denied, and temporary integration-test accounts are removed after testing.

To create your first account securely, run from a terminal with SSH access to the Deck:

```sh
ssh -t deck@100.97.7.84 'podman exec -it joco-svn htpasswd -B /etc/joco/users imdad'
```

Enter the new CAD password at the prompt; do not put it in a command, GitHub, or chat. Use unique usernames for each student. All authenticated users currently have read/write access to this one robot repository. Passwords are bcrypt hashes; configuration stays on the Deck, not in Git. Changing an account password uses the same command. Account administration and lock recovery are mentor-only SSH tasks.

## Lock enforcement

The pre-commit hook requires a lock owned by the submitting user for modifications, property changes, deletion, or replacement of existing `.SLDPRT`, `.SLDASM`, and `.SLDDRW` files (case-insensitive). SVN additionally checks the client's lock token. Newly committed CAD must have `svn:needs-lock` and `svn:mime-type=application/octet-stream`.

Pre-lock and pre-unlock hooks prevent force-stealing and force-breaking locks. Directory deletion/replacement is blocked in this initial version so it cannot bypass per-file ownership. Mentor recovery/renames will need an explicit controlled workflow. A lock is per file, not per assembly tree. These checks protect repository writes; SOLIDWORKS read-only transitions still need to be implemented and verified on Windows.

## Validation

Run the integration test only during setup/maintenance, with no concurrent account administration. It temporarily creates two users in the password file and a disposable repository, then restores the password file and removes the test repository in `finally`:

```sh
podman exec -i joco-svn python3 - < ~/server/joco-cad/source/tests/integration.py
```

The tests exercise real Apache HTTP checkout, property requirements, read-only working copies, unlocked change rejection, competing locks, force-lock/unlock rejection, lock release on commit, updates, deletion restrictions, and hotcopy restoration. Fixture files only use CAD extensions; they are not SOLIDWORKS documents. These tests do not establish Windows/SharpSvn compatibility or tunnel compatibility.

## Backups

`joco-svn-backup.timer` creates a verified repository hotcopy plus authentication/configuration and deployment source each day, retaining completed backups for 14 days under `~/server/joco-cad/backups`. Failed backups stay for inspection. The initial backup is run during deployment. These copies are on the same SSD: an off-device destination must be configured before storing irreplaceable CAD.

```sh
systemctl --user start joco-svn-backup.service
journalctl --user -u joco-svn-backup.service -n 30
```

For disaster recovery, stop the SVN service, preserve the damaged data separately, copy a verified backup repository into `data/2027-Robot`, restore configuration if needed, and restart. Do not overlay repository database files while the service is running. Locks and existing working copies require review after recovery; do not silently break locks to repair them.

## Cloudflare step still pending

The planned public route is `cad.imdad.stream` → a Cloudflare Tunnel connector on this Deck → `http://127.0.0.1:8091`, retaining the `/svn/2027-Robot` path. Run the connector with host networking if containerized so its loopback reaches this bound service. Keep its token in a restricted file, not in the repository or command logs.

Before declaring the public endpoint ready, test real SVN checkout, lock, commit, and update through HTTPS, including representative assembly sizes. Avoid a browser-only login challenge in the SVN path; SVN has its own per-user authentication. Cloudflare request limits and timeouts may affect large commits. The public route and connector are not configured by these files. See [Cloudflare's tunnel setup documentation](https://developers.cloudflare.com/tunnel/get-started/).

## Operations

```sh
systemctl --user status joco-svn.service
podman logs --tail 30 joco-svn
systemctl --user list-timers joco-svn-backup.timer
```

To rebuild, copy this directory to `~/server/joco-cad/source`, build `podman build -t localhost/joco-svn:initial ~/server/joco-cad/source`, then restart the service during maintenance. Take a backup first. This initial image tag is local; use immutable release tags for future production upgrades. Do not restart during a commit.
