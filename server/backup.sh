#!/bin/sh
set -eu
# JOCO_HOME: the server's folder (Team 5919's Deck: ~/server/joco-cad). JOCO_ENGINE: podman or docker (default: whichever exists).
home="${JOCO_HOME:-$HOME/server/joco-cad}"
engine="${JOCO_ENGINE:-$(command -v podman >/dev/null 2>&1 && echo podman || echo docker)}"
backup_root="$home/backups"
mkdir -p "$backup_root"
chmod 700 "$backup_root"
exec 9>"$backup_root/.lock"
flock -n 9 || exit 0
stamp=$(date -u +%Y%m%dT%H%M%SZ)
destination="$backup_root/$stamp"
mkdir "$destination"
# Every repository: all seasons plus the Library. hotcopy is consistent while the server runs.
for repo in $($engine exec joco-svn sh -c 'for d in /var/lib/svn/*/format; do basename "$(dirname "$d")"; done'); do
    $engine exec joco-svn svnadmin hotcopy "/var/lib/svn/$repo" "/tmp/joco-backup-$stamp-$repo"
    $engine exec joco-svn svnadmin verify -q "/tmp/joco-backup-$stamp-$repo"
    $engine cp "joco-svn:/tmp/joco-backup-$stamp-$repo" "$destination/$repo"
    $engine exec joco-svn rm -rf "/tmp/joco-backup-$stamp-$repo"
done
# Config files belong to the container's web user; read them from inside. Restore: copy back, restart (startup fixes ownership).
mkdir -m 700 "$destination/config"
$engine exec joco-svn tar -C /etc/joco -cf - . | tar -xf - -C "$destination/config"
# The server's source as deployed, when it's kept next to the data (Team 5919's Deck).
[ -d "$home/source" ] && cp -a "$home/source" "$destination/source"
touch "$destination/COMPLETE"
printf 'Verified backup: %s\n' "$destination"
# Shown on the mentor page's Health panel; only written after a verified backup.
printf '{"time": "%s", "folder": "%s"}\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$stamp" |
    $engine exec -i joco-svn sh -c 'mkdir -p /etc/joco/health && cat > /etc/joco/health/backup.json && chmod 644 /etc/joco/health/backup.json' || true
# Keep completed daily backups for 14 days; leave failed/incomplete backups for review.
find "$backup_root" -mindepth 2 -maxdepth 2 -name COMPLETE -mtime +14 -print | while IFS= read -r marker; do
    rm -rf "${marker%/COMPLETE}"
done
