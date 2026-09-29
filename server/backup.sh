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
# svnadmin hotcopy takes a repository-consistent snapshot while the server runs.
podman exec joco-svn svnadmin hotcopy /var/lib/svn/2027-Robot "/tmp/joco-backup-$stamp"
podman exec joco-svn svnadmin verify "/tmp/joco-backup-$stamp"
podman cp "joco-svn:/tmp/joco-backup-$stamp" "$destination/2027-Robot"
podman exec joco-svn rm -rf "/tmp/joco-backup-$stamp"
cp -a "$HOME/server/joco-cad/config" "$destination/config"
cp -a "$HOME/server/joco-cad/source" "$destination/source"
touch "$destination/COMPLETE"
printf 'Verified backup: %s\n' "$destination"
# Keep completed daily backups for 14 days; leave failed/incomplete backups for review.
find "$backup_root" -mindepth 2 -maxdepth 2 -name COMPLETE -mtime +14 -print | while IFS= read -r marker; do
    rm -rf "${marker%/COMPLETE}"
done
