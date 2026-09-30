#!/bin/sh
# Disaster-recovery drill: proves a backup can actually be restored. Never touches the live server.
#
#   restore-drill.sh BACKUP_FOLDER [--report]
#
# BACKUP_FOLDER is one completed backup: ~/server/joco-cad/backups/<stamp> on the Deck, or
# ~/Backups/joco-cad/<stamp> on the off-device computer. The drill copies it to a temporary folder, verifies every
# repository, starts a throwaway server from the copy on a spare local port, and checks that history, files, locks,
# accounts, and settings are all there and that the server answers. --report records the result on the mentor
# page's Health panel (on the Deck directly; elsewhere over SSH like offsite-pull.sh).
#
# Environment: JOCO_ENGINE (podman or docker; default: whichever exists), JOCO_IMAGE (default: the image the live
# server runs, or joco-svn:test), JOCO_DECK (for --report from another computer).
set -eu
backup="${1:?usage: restore-drill.sh BACKUP_FOLDER [--report]}"
report="${2:-}"
[ -e "$backup/COMPLETE" ] || { echo "Not a completed backup: $backup" >&2; exit 1; }
engine="${JOCO_ENGINE:-$(command -v podman >/dev/null 2>&1 && echo podman || echo docker)}"
image="${JOCO_IMAGE:-$($engine ps --filter name=^joco-svn$ --format '{{.Image}}' 2>/dev/null | head -1)}"
image="${image:-joco-svn:test}"
work=$(mktemp -d "${TMPDIR:-/tmp}/joco-drill.XXXXXX")
container="joco-drill-$$"
cleanup() {
    $engine rm -f "$container" >/dev/null 2>&1 || true
    # The container made the copy root/www-data owned; remove it from inside the same image.
    $engine run --rm --entrypoint rm -v "$work:/w" "$image" -rf /w/data /w/config >/dev/null 2>&1 || true
    rm -rf "$work"
}
trap cleanup EXIT
fail() { echo "DRILL FAILED: $*" >&2; exit 1; }

echo "Restoring $(basename "$backup") with $engine and $image"
mkdir -p "$work/data" "$work/config"
repos=""
for format in "$backup"/*/format; do
    repo=$(basename "$(dirname "$format")")
    cp -a "$backup/$repo" "$work/data/$repo"
    repos="$repos $repo"
done
[ -n "$repos" ] || fail "no repositories in the backup"
cp -a "$backup/config/." "$work/config/"

port=$((18200 + $$ % 700))
$engine run -d --name "$container" -p "127.0.0.1:$port:80" -v "$work/data:/var/lib/svn" -v "$work/config:/etc/joco" "$image" >/dev/null
for i in $(seq 60); do
    code=$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$port/svn/$(echo $repos | cut -d' ' -f1)/" || true)
    [ "$code" = 401 ] && break
    sleep 1
done
[ "$code" = 401 ] || fail "the restored server did not answer (HTTP $code)"
x() { $engine exec "$container" "$@"; }

summary=""
for repo in $repos; do
    x svnadmin verify -q "/var/lib/svn/$repo" || fail "$repo did not verify"
    youngest=$(x svnlook youngest "/var/lib/svn/$repo")
    files=$(x svn list -R "file:///var/lib/svn/$repo" | grep -vc '/$' || true)
    history=$(x svn log -q "file:///var/lib/svn/$repo" | grep -c '^r' || true)
    locks=$(x svnadmin lslocks "/var/lib/svn/$repo" | grep -c '^Path: ' || true)
    x runuser -u www-data -- svn export -q --force "file:///var/lib/svn/$repo" "/tmp/export-$repo" || fail "$repo files could not be read back"
    [ "$history" -ge 1 ] || fail "$repo has no history"
    echo "  $repo: r$youngest, $files files, $history revisions, $locks locks — verified and readable"
    summary="$summary $repo r$youngest;"
done
# Settings and accounts came back, and the server uses them.
x test -s /etc/joco/users || fail "no accounts in the restored settings"
x python3 -c "import json; s = json.load(open('/etc/joco/state.json')); assert 'mentors' in s" || fail "settings (state.json) unreadable"
accounts=$(x sh -c 'grep -c : /etc/joco/users')
curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$port/catalog.json" | grep -q 401 || fail "catalog is not protected"
x test -s /etc/joco/public/catalog.json || fail "the restored server did not publish its catalog"
echo "  settings: $accounts accounts, state and catalog restored"
echo "DRILL PASSED: $(basename "$backup")"

if [ "$report" = "--report" ]; then
    record=$(printf '{"time": "%s", "backup": "%s", "host": "%s", "result": "%s"}' \
        "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$(basename "$backup")" "$(uname -n)" "passed:$summary")
    if $engine ps --format '{{.Names}}' 2>/dev/null | grep -qx joco-svn; then
        echo "$record" | $engine exec -i joco-svn sh -c 'mkdir -p /etc/joco/health && cat > /etc/joco/health/restore-test.json && chmod 644 /etc/joco/health/restore-test.json'
    else
        echo "$record" | ssh -o BatchMode=yes -o ConnectTimeout=20 "${JOCO_DECK:-deck@100.97.7.84}" \
            "podman exec -i joco-svn sh -c 'mkdir -p /etc/joco/health && cat > /etc/joco/health/restore-test.json && chmod 644 /etc/joco/health/restore-test.json'"
    fi
    echo "Recorded on the mentor page's Health panel."
fi
