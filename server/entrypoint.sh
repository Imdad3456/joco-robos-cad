#!/bin/sh
set -eu
mkdir -p /var/lib/svn /etc/joco
if [ ! -f /etc/joco/users ]; then
    touch /etc/joco/users
fi
chmod 640 /etc/joco/users
chown root:www-data /etc/joco/users
if [ ! -d /var/lib/svn/2027-Robot ]; then
    svnadmin create /var/lib/svn/2027-Robot
    svn mkdir --non-interactive -m 'Create canonical robot folders' \
        file:///var/lib/svn/2027-Robot/00_Master \
        file:///var/lib/svn/2027-Robot/10_Drivetrain \
        file:///var/lib/svn/2027-Robot/20_Intake \
        file:///var/lib/svn/2027-Robot/30_Shooter \
        file:///var/lib/svn/2027-Robot/40_Climber \
        file:///var/lib/svn/2027-Robot/50_Electrical \
        file:///var/lib/svn/2027-Robot/90_COTS
fi
cp /opt/joco/pre-* /var/lib/svn/2027-Robot/hooks/
chmod 755 /var/lib/svn/2027-Robot/hooks/pre-*
chown -R www-data:www-data /var/lib/svn
exec apachectl -D FOREGROUND
