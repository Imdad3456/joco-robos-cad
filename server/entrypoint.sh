#!/bin/sh
set -eu
# Per-team settings (see docs/SETUP.md). Every one has a default, so a server started without them still runs.
export JOCO_SERVER_NAME="${JOCO_SERVER_NAME:-localhost}"
JOCO_TEAM_NAME="${JOCO_TEAM_NAME:-A FIRST Robotics Competition team}"
first_season="${JOCO_FIRST_SEASON:-$(date +%Y)-Robot}"
# The public front page names the team.
JOCO_TEAM_NAME="$JOCO_TEAM_NAME" python3 -c 'import html, os, sys; sys.stdout.write(open("/opt/joco/www/index.html").read().replace("{{TEAM}}", html.escape(os.environ["JOCO_TEAM_NAME"])))' \
    > /var/www/joco/index.html
mkdir -p /var/lib/svn /etc/joco/public/updates
touch /etc/joco/users
# The admin service (www-data) manages accounts and generated files; Apache reads them.
chown -R www-data:www-data /var/lib/svn /etc/joco
chmod 600 /etc/joco/users
if [ "$first_season" != none ] && ! ls /var/lib/svn/*/format >/dev/null 2>&1 && [ ! -f /etc/joco/state.json ]; then
    # First start only: one robot season to begin with (none: the team imports its existing CAD instead). Later seasons come from the mentor page.
    runuser -u www-data -- svnadmin create "/var/lib/svn/$first_season"
    runuser -u www-data -- svnmucc --non-interactive --username joco-admin -m 'Create canonical robot folders' \
        -U "file:///var/lib/svn/$first_season" mkdir 00_Master mkdir 10_Drivetrain mkdir 20_Intake \
        mkdir 30_Shooter mkdir 40_Climber mkdir 50_Electrical mkdir 90_COTS
fi
runuser -u www-data -- python3 /opt/joco/joco.py ensure
# Keep the mentor page running; SVN service does not depend on it.
(while true; do runuser -u www-data -- python3 /opt/joco/joco.py serve || true; sleep 3; done) &
exec apachectl -D FOREGROUND
