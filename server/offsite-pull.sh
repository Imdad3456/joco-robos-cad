#!/bin/sh
# Runs on a second computer (not the Deck). Pulls each completed, verified Deck backup
# that is not here yet, over SSH. The Deck cannot delete or change copies stored here.
set -eu
deck="${JOCO_DECK:-deck@100.97.7.84}"
store="${JOCO_OFFSITE_DIR:-$HOME/Backups/joco-cad}"
keep_days="${JOCO_OFFSITE_DAYS:-90}"
mkdir -p "$store"
chmod 700 "$store"
exec 9>"$store/.lock"
flock -n 9 || exit 0
for name in $(ssh -o BatchMode=yes -o ConnectTimeout=20 "$deck" \
        'cd ~/server/joco-cad/backups && for d in */COMPLETE; do [ -e "$d" ] && dirname "$d"; done'); do
    case "$name" in *[!0-9TZ]*) continue ;; esac
    [ -e "$store/$name/COMPLETE" ] && continue
    rm -rf "$store/.partial-$name"
    rsync -a -e 'ssh -o BatchMode=yes' "$deck:server/joco-cad/backups/$name/" "$store/.partial-$name/"
    [ -e "$store/.partial-$name/COMPLETE" ] || { echo "Incomplete copy of $name" >&2; exit 1; }
    mv "$store/.partial-$name" "$store/$name"
    printf 'Copied off-device: %s\n' "$store/$name"
done
latest=$(ls -1d "$store"/2*/COMPLETE 2>/dev/null | tail -1)
[ -n "$latest" ] || { echo 'No off-device backup exists yet' >&2; exit 1; }
# Report to the mentor page's Health panel (best effort).
printf '{"time": "%s", "latest": "%s", "host": "%s"}\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$(basename "$(dirname "$latest")")" "$(hostname)" |
    ssh -o BatchMode=yes -o ConnectTimeout=20 "$deck" \
    "podman exec -i joco-svn sh -c 'mkdir -p /etc/joco/health && cat > /etc/joco/health/offsite.json && chmod 644 /etc/joco/health/offsite.json'" || true
# Keep 90 days here, but never delete the newest backup.
find "$store" -mindepth 2 -maxdepth 2 -name COMPLETE -mtime +"$keep_days" -print | while IFS= read -r marker; do
    [ "$marker" = "$latest" ] || rm -rf "${marker%/COMPLETE}"
done
