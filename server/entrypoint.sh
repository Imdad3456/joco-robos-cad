#!/bin/sh
set -eu
mkdir -p /var/lib/svn /etc/joco/public/updates
touch /etc/joco/users
# The admin service (www-data) manages accounts and generated files; Apache reads them.
chown -R www-data:www-data /var/lib/svn /etc/joco
chmod 600 /etc/joco/users
if [ ! -d /var/lib/svn/2027-Robot ] && [ ! -f /etc/joco/state.json ]; then
    # First deployment only; later seasons are created from the admin page.
    runuser -u www-data -- svnadmin create /var/lib/svn/2027-Robot
    runuser -u www-data -- svnmucc --non-interactive --username joco-admin -m 'Create canonical robot folders' \
        -U file:///var/lib/svn/2027-Robot mkdir 00_Master mkdir 10_Drivetrain mkdir 20_Intake \
        mkdir 30_Shooter mkdir 40_Climber mkdir 50_Electrical mkdir 90_COTS
fi
runuser -u www-data -- python3 /opt/joco/joco.py ensure
# Keep the mentor page running; SVN service does not depend on it.
(while true; do runuser -u www-data -- python3 /opt/joco/joco.py serve || true; sleep 3; done) &
exec apachectl -D FOREGROUND
