# Set up CAD Hub for your team

CAD Hub is two pieces: a **SOLIDWORKS add-in** each student installs, and a **team server** a mentor runs. This guide sets up the server and gets your first student working. Plan on an afternoon. It's written by FRC Team 5919, who use it every day; questions and fixes are welcome as GitHub issues.

## What you need

- **A computer that stays on**, running Linux with Docker. An old desktop or laptop is plenty for a team: the server is Apache plus Subversion, and it uses very little memory. It's been run on x86-64 Linux (Team 5919 uses a Steam Deck with Podman). Disk: a few times the size of your robot CAD.
- **A web address with HTTPS**, such as `cad.yourteam.org`. Students' passwords go with every request, so the server must never be reachable over plain `http://`. The easiest way is a domain on Cloudflare and a free Cloudflare Tunnel (step 4): no router changes, and it works from home, school, and competitions.
- **SOLIDWORKS 2026** on the students' Windows PCs. (The FIRST sponsorship license works.)
- Optional: an **Onshape API key** if you want FRCDesignLib parts in the add-in's Library tab.

## 1. Get the server running

```bash
git clone https://github.com/Imdad3456/joco-robos-cad.git
cd joco-robos-cad/server
cp team.env.example team.env
nano team.env          # your web address, team name, first season (for example 2026-Robot)
docker compose up -d --build
```

Check it answers: `curl -s http://127.0.0.1:8091/ | head` shows your team's front page. Your robot repositories live in `server/data` and settings and accounts in `server/config`. **Those two folders are everything; back them up** (step 6).

## 2. Make yourself the first mentor

```bash
docker exec -it -u www-data joco-svn htpasswd -B /etc/joco/users yourname
docker exec -u www-data joco-svn python3 /opt/joco/joco.py mentor add yourname
```

Pick a long password. Every other account (students and other mentors) is made from the mentor page.

## 3. Bring in your robot (optional)

The first start makes an empty season with standard folders (`00_Master`, `10_Drivetrain`, …). To start from CAD you already have instead, set `JOCO_FIRST_SEASON=none` in `team.env` **before the first start**, then copy the CAD folder to the server and import it:

```bash
docker cp "/path/to/Robot CAD/." joco-svn:/tmp/import
docker exec -u www-data joco-svn python3 /opt/joco/joco.py import-season 2026-Robot /tmp/import yourname "Robot.SLDASM"
docker exec joco-svn rm -rf /tmp/import
```

The import checks every file arrived byte for byte. The last argument (optional) is the top-level assembly that **Open Robot** opens, relative to the folder you imported. Without it, Open Robot opens `00_Master/Robot.SLDASM`. Already started with an empty season? Import under another name (like `2026-Imported`) and make it active on the mentor page's Seasons tab.

## 4. Put it on the internet with HTTPS

### Cloudflare Tunnel (recommended)

1. Add your domain to a free Cloudflare account.
2. In the Cloudflare dashboard: **Zero Trust → Networks → Tunnels → Create a tunnel** (type cloudflared). Install `cloudflared` on the server with the command it shows.
3. Add a **public hostname**: `cad.yourteam.org`, service `HTTP`, URL `localhost:8091`.

Cloudflare provides the HTTPS certificate. Nothing on your network has to be opened.

### Your own reverse proxy

If you already run a web server with HTTPS (Caddy, nginx, Traefik), send `cad.yourteam.org` to `http://127.0.0.1:8091`. With Caddy that's one line: `cad.yourteam.org { reverse_proxy 127.0.0.1:8091 }`. Allow uploads up to 100 MB.

**Then:** `https://cad.yourteam.org` shows your front page, and `https://cad.yourteam.org/admin` asks you to sign in and opens the mentor page.

**School networks:** some web filters block brand-new or unusual domains (Team 5919 had one block a `.stream` address). A plain `.org`, plus asking the filter companies to categorize your address as Education (Fortinet FortiGuard, Palo Alto, Cisco Talos, Symantec Site Review), avoids most of it.

## 5. Set up the mentor page

On `https://cad.yourteam.org/admin`:

- **Add-in tab → Team SOLIDWORKS version:** set the year your team uses (for example 2026). A newer SOLIDWORKS can then look at the robot but not save into it, so nobody upgrades the shared files by accident.
- **Add-in tab → Upload manually:** download the newest `JOCO-ROBOS-CAD-Setup-x.y.z.exe` from [Releases](https://github.com/Imdad3456/joco-robos-cad/releases), upload it with the SHA-256 from its release notes, and click **Publish**. From then on, students' add-ins offer every version you release here, and nothing else. Repeat when a new version comes out.
- **Seasons tab:** your first season is there and active. Create next year's here when it's time.

## 6. Backups

`server/backup.sh` makes a verified copy of every repository plus the settings, keeps 14 days, and reports on the mentor page's Health panel. Run it daily, for example with cron:

```bash
crontab -e
# add:
30 3 * * * JOCO_HOME=/path/to/joco-robos-cad/server /bin/sh /path/to/joco-robos-cad/server/backup.sh
```

Backups land in `server/backups`. **Copy them to another computer too.** `server/offsite-pull.sh` does that from the other computer over SSH: set `JOCO_DECK=user@server` and `JOCO_REMOTE_BACKUPS=path/to/joco-robos-cad/server/backups` (relative to that user's home), and run it daily. Once a season, prove a backup restores: `server/restore-drill.sh server/backups/<newest> --report` starts a throwaway server from it and checks everything.

## 7. Your first student

1. They download and run the installer from [Releases](https://github.com/Imdad3456/joco-robos-cad/releases/latest). Windows warns about an unknown publisher: **More info → Run anyway**.
2. They start SOLIDWORKS. It asks for the **team server**: they type `cad.yourteam.org`.
3. They choose a username and password and click **Send request**.
4. On the mentor page, **Accounts** shows the request. Give them the code; they type it in and click **Finish**.
5. **Open Robot**. They're in.

On school PCs, IT can preset the server for every Windows user: registry value `Server` (string, `https://cad.yourteam.org/`) under `HKEY_LOCAL_MACHINE\Software\JOCO ROBOS\CAD`.

Then hand out the student section of the [README](../README.md#for-students), and run through [TESTING.md](../TESTING.md) with two people before the season gets busy.

## Updating the server

```bash
cd joco-robos-cad && git pull
cd server && JOCO_HOME="$PWD" /bin/sh backup.sh && docker compose up -d --build
```

Your data and accounts stay in `data/` and `config/`. Add-in updates are separate: upload the new installer on the Add-in tab (step 5).

## How it works

[HOW-IT-WORKS.md](HOW-IT-WORKS.md) explains locks, Submit, updates and recovery. The short version: every student keeps a full Subversion working copy of the robot; the server keeps history, hands out exclusive locks on SOLIDWORKS files, and refuses any change to a file someone else holds.
