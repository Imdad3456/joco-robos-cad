#!/bin/sh
# Deploys this checkout's server/ to Team 5919's Deck as a new image version, keeping the previous one for rollback.
#   sh tools/deploy-server.sh v31            (from the repository folder, in Git Bash)
# Rollback: on the Deck, put the previous image tag back in ~/.config/systemd/user/joco-svn.service (a copy of the old unit is
# saved as ~/server/joco-cad/joco-svn.service.<old>), then: systemctl --user daemon-reload && systemctl --user restart joco-svn
set -eu
new="${1:?usage: deploy-server.sh vNN}"
deck="${JOCO_DECK:-imdad3456@192.168.1.19}"
cd "$(dirname "$0")/.."
echo "Copying server/ from $(git log --oneline -1) to $deck…"
git archive --format=tar HEAD:server | ssh -o BatchMode=yes "$deck" 'cd ~/server/joco-cad && rm -rf source.new && mkdir source.new && tar -x -C source.new'
ssh -o BatchMode=yes "$deck" "NEW=$new sh -s" <<'REMOTE'
set -eu
cd ~/server/joco-cad
unit=~/.config/systemd/user/joco-svn.service
old=$(grep -o 'joco-svn:v[0-9]*' "$unit" | cut -d: -f2)
[ "$old" != "$NEW" ] || { echo "Already on $NEW"; exit 1; }
# Files come from a Windows checkout: Linux needs plain line endings.
find source.new -type f \( -name '*.py' -o -name '*.sh' -o -name '*.conf' -o -name Containerfile -o -name '*.html' -o -name '*.txt' \
    -o -name '*.service' -o -name '*.timer' -o -name '*.md' -o -name '*.yaml' -o -name '*.example' \) -exec sed -i 's/\r$//' {} +
echo "Changed files:"; diff -rq source source.new || true
echo "Building $NEW…"
(cd source.new && podman build -t "localhost/joco-svn:$NEW" -f Containerfile . > ../build.log 2>&1) || { echo "Build failed: see ~/server/joco-cad/build.log"; exit 1; }
mv source "source.$old" && mv source.new source
cp "$unit" "joco-svn.service.$old"
sed -i "s#localhost/joco-svn:$old#localhost/joco-svn:$NEW#" "$unit"
systemctl --user daemon-reload && systemctl --user restart joco-svn.service
for i in $(seq 1 40); do curl -s -o /dev/null http://127.0.0.1:8091/ && break; sleep 1; done
sleep 2
echo "Service: $(systemctl --user is-active joco-svn.service), image $(grep -o 'joco-svn:v[0-9]*' "$unit") (was $old)"
REMOTE
