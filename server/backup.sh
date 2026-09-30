#!/bin/sh
set -eu
backup_root="$HOME/server/joco-cad/backups"
mkdir -p "$backup_root"
chmod 700 "$backup_root"
exec 9>"$backup_root/.lock"
flock -n 9 || exit 0
stamp=$(date -u +%Y%m%dT%H%M%SZ)
destination="$backup_root/$stamp"
mkdir "$destination"
# Every repository: all seasons plus the Library. hotcopy is consistent while the server runs.
for repo in $(podman exec joco-svn sh -c 'for d in /var/lib/svn/*/format; do basename "$(dirname "$d")"; done'); do
    podman exec joco-svn svnadmin hotcopy "/var/lib/svn/$repo" "/tmp/joco-backup-$stamp-$repo"
    podman exec joco-svn svnadmin verify -q "/tmp/joco-backup-$stamp-$repo"
    podman cp "joco-svn:/tmp/joco-backup-$stamp-$repo" "$destination/$repo"
    podman exec joco-svn rm -rf "/tmp/joco-backup-$stamp-$repo"
done
# Config files belong to the container's web user; read them from inside. Restore: copy back, restart (startup fixes ownership).
mkdir -m 700 "$destination/config"
podman exec joco-svn tar -C /etc/joco -cf - . | tar -xf - -C "$destination/config"
cp -a "$HOME/server/joco-cad/source" "$destination/source"
touch "$destination/COMPLETE"
printf 'Verified backup: %s\n' "$destination"
# Shown on the mentor page's Health panel; only written after a verified backup.
printf '{"time": "%s", "folder": "%s"}\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$stamp" |
    podman exec -i joco-svn sh -c 'mkdir -p /etc/joco/health && cat > /etc/joco/health/backup.json && chmod 644 /etc/joco/health/backup.json' || true
# Keep completed daily backups for 14 days; leave failed/incomplete backups for review.
find "$backup_root" -mindepth 2 -maxdepth 2 -name COMPLETE -mtime +14 -print | while IFS= read -r marker; do
    rm -rf "${marker%/COMPLETE}"
done
