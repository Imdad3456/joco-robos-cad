# Disposable-container test of the mentor page and multi-repository rules. Needs a test container named
# joco-test on 127.0.0.1:18091 with users mentor1/mentorpass123 (mentor) and sarah/sarahpass123. Never run against the Deck.
import base64, re, subprocess, urllib.request, urllib.parse, urllib.error, uuid
BASE = 'http://127.0.0.1:18091'
def req(path, user=None, pw=None, data=None, origin=BASE, ctype=None):
    r = urllib.request.Request(BASE + path, data=data)
    if user: r.add_header('Authorization', 'Basic ' + base64.b64encode(f'{user}:{pw}'.encode()).decode())
    if data is not None and origin: r.add_header('Origin', origin)
    if ctype: r.add_header('Content-Type', ctype)
    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, *a): return None
    try:
        resp = urllib.request.build_opener(NoRedirect).open(r)
        return resp.status, resp.headers, resp.read().decode()
    except urllib.error.HTTPError as e:
        return e.code, e.headers, e.read().decode(errors='replace')
M = ('mentor1', 'mentorpass123'); U = ('sarah', 'sarahpass123')
n = 0
def check(cond, msg):
    global n
    if not cond: raise SystemExit('FAIL: ' + msg)
    n += 1
def svn(*args, user=U):
    return subprocess.run(['docker', 'exec', 'joco-test', 'sh', '-c', ' '.join(args) + f' --non-interactive --no-auth-cache --username {user[0]} --password {user[1]}'], capture_output=True, text=True)
def post(path, fields, user=M, origin=BASE):
    s, h, b = req(path, *user)
    tok = re.search(r'name="token" value="([0-9a-f]+)"', b).group(1)
    s, h, b = req(path, *user, data=urllib.parse.urlencode(dict(fields, token=tok)).encode(), origin=origin, ctype='application/x-www-form-urlencoded')
    loc = urllib.parse.unquote(h.get('Location', ''))
    return s, loc

check(req('/catalog.json')[0] == 401, 'anonymous catalog')
check(req('/admin')[0] == 401, 'anonymous admin')
check(req('/svn/2027-Robot/')[0] == 401, 'anonymous svn')
s, _, b = req('/catalog.json', *U); check(s == 200 and '"active": "2027-Robot"' in b, 'student reads catalog')
check(req('/admin', *U)[0] == 403, 'student blocked from admin')
check(req('/admin', 'sarah', 'wrong')[0] == 401, 'bad password')
r = urllib.request.Request(BASE + '/admin'); r.add_header('Authorization', 'Basic ' + base64.b64encode(b'sarah:sarahpass123').decode()); r.add_header('X-Joco-User', 'mentor1')
try: urllib.request.urlopen(r); check(False, 'header spoof')
except urllib.error.HTTPError as e: check(e.code == 403, 'spoofed X-Joco-User ignored')
for p in ('/admin', '/admin/locks', '/admin/library', '/admin/users'):
    check(req(p, *M)[0] == 200, 'mentor page ' + p)
# CSRF
s, loc = post('/admin', {'action': 'create-season', 'name': '2028-Robot'}, origin='https://evil.example')
check('error=' in loc and 'did not come' in loc, 'cross-origin post refused')
s, h, b = req('/admin', *M, data=b'action=create-season&name=2028-Robot&token=bad', ctype='application/x-www-form-urlencoded')
check('expired' in urllib.parse.unquote(h['Location']), 'bad token refused')
# Seasons
s, loc = post('/admin', {'action': 'create-season', 'name': '2028-Robot', 'activate': '1'}); check('ok=Created' in loc, 'create season ' + loc)
s, loc = post('/admin', {'action': 'create-season', 'name': '../etc'}); check('error=' in loc, 'bad season name')
s, _, b = req('/catalog.json', *U); check('"active": "2028-Robot"' in b, 'catalog active switched')
out = svn('svn ls http://localhost/svn/2028-Robot'); check('90_COTS/' in out.stdout, 'new season folders ' + out.stderr)
out = svn('svn propget svn:needs-lock http://localhost/svn/2028-Robot/00_Master'); 
# Hooks installed in new season: commit unlocked-new CAD without props must fail
svn('rm -rf /tmp/wc; svn co http://localhost/svn/2028-Robot /tmp/wc')
subprocess.run(['docker', 'exec', 'joco-test', 'sh', '-c', 'echo part > /tmp/wc/10_Drivetrain/Plate.SLDPRT && svn add -q /tmp/wc/10_Drivetrain/Plate.SLDPRT'])
out = svn('svn ci -m noprops /tmp/wc'); check(out.returncode != 0 and 'needs-lock' in out.stderr, 'new season has hooks')
subprocess.run(['docker', 'exec', 'joco-test', 'sh', '-c', 'svn ps -q svn:needs-lock "*" /tmp/wc/10_Drivetrain/Plate.SLDPRT && svn ps -q svn:mime-type application/octet-stream /tmp/wc/10_Drivetrain/Plate.SLDPRT'])
out = svn('svn ci -m "Add plate" /tmp/wc'); check(out.returncode == 0, 'commit with props ' + out.stderr)
out = svn('svn lock -m edit /tmp/wc/10_Drivetrain/Plate.SLDPRT'); check(out.returncode == 0, 'lock ' + out.stderr)
s, _, b = req('/admin/locks', *M); check('Plate.SLDPRT' in b and 'sarah' in b, 'lock listed')
# Archive refused while active / locked
s, loc = post('/admin', {'action': 'archive', 'name': '2028-Robot'}); check('Make another season active' in loc, 'cannot archive active')
s, loc = post('/admin/locks', {'action': 'release-lock', 'repo': '2028-Robot', 'path': '/10_Drivetrain/Plate.SLDPRT'}); check('ok=Released' in loc, 'release lock ' + loc)
check('Plate.SLDPRT' not in req('/admin/locks', *M)[2], 'lock gone')
# Library upload (multipart) and promote
boundary = uuid.uuid4().hex
s, _, b = req('/admin/library', *M); tok = re.search(r'name="token" value="([0-9a-f]+)"', b).group(1)
body = (f'--{boundary}\r\nContent-Disposition: form-data; name="token"\r\n\r\n{tok}\r\n'
        f'--{boundary}\r\nContent-Disposition: form-data; name="folder"\r\n\r\nMotors/Kraken\r\n'
        f'--{boundary}\r\nContent-Disposition: form-data; name="files"; filename="Kraken X60.SLDPRT"\r\nContent-Type: application/octet-stream\r\n\r\n').encode() + bytes(range(256)) * 50 + f'\r\n--{boundary}--\r\n'.encode()
s, h, _ = req('/admin/library', *M, data=body, ctype='multipart/form-data; boundary=' + boundary)
check('ok=Added 1' in urllib.parse.unquote(h['Location']), 'upload ' + urllib.parse.unquote(h['Location']))
out = subprocess.run(['docker', 'exec', 'joco-test', 'sh', '-c', 'svn cat --non-interactive --no-auth-cache --username sarah --password sarahpass123 "http://localhost/svn/Library/Motors/Kraken/Kraken X60.SLDPRT" | md5sum'], capture_output=True, text=True)
exp = subprocess.run(['md5sum'], input=bytes(range(256)) * 50, capture_output=True).stdout.decode().split()[0]
check(exp in out.stdout, 'uploaded bytes intact')
out = svn('svn propget svn:needs-lock "http://localhost/svn/Library/Motors/Kraken/Kraken X60.SLDPRT"'); check('*' in out.stdout, 'library props')
s, h, _ = req('/admin/library', *M, data=body, ctype='multipart/form-data; boundary=' + boundary)
check('already exists' in urllib.parse.unquote(h['Location']), 'duplicate upload refused')
s, loc = post('/admin/library', {'action': 'promote', 'choice': '2028-Robot|10_Drivetrain/Plate.SLDPRT', 'folder': 'Mechanisms'}); check('ok=Added Mechanisms/Plate.SLDPRT' in loc, 'promote ' + loc)
s, loc = post('/admin/library', {'action': 'promote', 'choice': '2028-Robot|../../etc/passwd', 'folder': 'Mechanisms'}); check('error=' in loc, 'promote traversal refused')
out = svn('svn log -l1 http://localhost/svn/Library'); check('mentor1' in out.stdout, 'library author is mentor')
check('Kraken X60.SLDPRT' in req('/admin/library', *M)[2], 'library listed')
# Student can add to library via SVN
subprocess.run(['docker', 'exec', 'joco-test', 'sh', '-c', 'rm -rf /tmp/lib'])
svn('svn co http://localhost/svn/Library /tmp/lib')
subprocess.run(['docker', 'exec', 'joco-test', 'sh', '-c', 'echo x > /tmp/lib/Hardware/Bolt.SLDPRT && svn add -q /tmp/lib/Hardware/Bolt.SLDPRT && svn ps -q svn:needs-lock "*" /tmp/lib/Hardware/Bolt.SLDPRT && svn ps -q svn:mime-type application/octet-stream /tmp/lib/Hardware/Bolt.SLDPRT'])
out = svn('svn ci -m "Add bolt" /tmp/lib'); check(out.returncode == 0, 'student library submit ' + out.stderr)
# Archive 2027 -> read-only
s, loc = post('/admin', {'action': 'archive', 'name': '2027-Robot'}); check('read-only' in loc, 'archive ' + loc)
s, _, b = req('/catalog.json', *U); check('"archived": true' in b, 'catalog archived')
svn('rm -rf /tmp/old; svn co http://localhost/svn/2027-Robot /tmp/old'); 
out = svn('svn mkdir -m x http://localhost/svn/2027-Robot/99_Test'); check(out.returncode != 0 and ('403' in out.stderr or 'forbidden' in out.stderr.lower() or 'authoriz' in out.stderr.lower()), 'archived write denied ' + out.stderr)
out = svn('svn ls http://localhost/svn/2027-Robot'); check('00_Master/' in out.stdout, 'archived still readable')
s, loc = post('/admin', {'action': 'activate', 'name': '2027-Robot'}); check('Unarchive' in loc, 'cannot activate archived')
s, loc = post('/admin', {'action': 'unarchive', 'name': '2027-Robot'}); check('editable' in loc, 'unarchive')
out = svn('svn mkdir -m x http://localhost/svn/2027-Robot/99_Test'); check(out.returncode == 0, 'unarchived writable ' + out.stderr)
# Accounts: setup codes, students choose their own passwords
import json as _json
def setup(user, code, password, raw=None):
    r = urllib.request.Request(BASE + '/account/setup', data=raw if raw is not None else _json.dumps({'username': user, 'code': code, 'password': password}).encode(), method='POST')
    r.add_header('Content-Type', 'application/json')
    try:
        resp = urllib.request.urlopen(r); return resp.status, resp.read().decode()
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode()
def code_for(user):
    page = req('/admin/users', *M)[2]
    m = re.search(r'<b>' + re.escape(user) + r'</b>.*?Setup code: <code[^>]*>([A-Z0-9-]+)</code>', page, re.S)
    return m.group(1) if m else None
s, loc = post('/admin/users', {'action': 'add-user', 'username': 'alex'}); check('ok=Added alex' in loc and 'setup code' in loc, 'add user')
code = code_for('alex'); check(code and re.match(r'^[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}$', code), 'setup code shown to mentor')
check(code not in loc, 'setup code never in a URL')
check(req('/catalog.json', 'alex', code)[0] == 401, 'the code is not a password')
check(setup('alex', 'AAAA-AAAA-AAAA', 'alexpassword1')[0] == 400, 'wrong code refused')
check(setup('nobody', code, 'alexpassword1')[0] == 400, 'wrong user refused')
check('at least 10' in setup('alex', code, 'short')[1], 'short password refused')
s_, t = setup('alex', code.lower().replace('-', ''), 'alexpassword1'); check(s_ == 200, 'student sets own password (code case/dashes forgiven) ' + t)
check(req('/catalog.json', 'alex', 'alexpassword1')[0] == 200, 'new user can sign in')
check(setup('alex', code, 'alexpassword9')[0] == 400, 'code works only once')
check(code_for('alex') is None, 'used code disappears')
check(req('/account/setup')[0] != 401, 'setup address reachable without sign-in (Apache lets it through)')
check(req('/admin/users')[0] == 401, 'admin still requires sign-in')
# change own password from the add-in
r = urllib.request.Request(BASE + '/admin/api/password', data=_json.dumps({'password': 'alexpassword2'}).encode(), method='POST')
r.add_header('Authorization', 'Basic ' + base64.b64encode(b'alex:alexpassword1').decode()); r.add_header('X-Joco-Client', 'addin')
check(urllib.request.urlopen(r).status == 200, 'change own password')
check(req('/catalog.json', 'alex', 'alexpassword1')[0] == 401 and req('/catalog.json', 'alex', 'alexpassword2')[0] == 200, 'password change takes effect')
r = urllib.request.Request(BASE + '/admin/api/password', data=_json.dumps({'password': 'hackedpassword'}).encode(), method='POST')
r.add_header('Authorization', 'Basic ' + base64.b64encode(b'alex:alexpassword2').decode())
try: urllib.request.urlopen(r); check(False, 'x')
except urllib.error.HTTPError as e: check(e.code == 403, 'password change needs the add-in header')
# reset: new code, old password dead immediately; guessing kills the code
s, loc = post('/admin/users', {'action': 'reset-password', 'username': 'alex'}); check('no longer works' in loc, 'reset')
check(req('/catalog.json', 'alex', 'alexpassword2')[0] == 401, 'reset disables old password')
code2 = code_for('alex'); check(code2 and code2 != code, 'reset makes a new code')
for i in range(5): setup('alex', 'AAAA-AAAA-AAA%d' % i, 'alexpassword3')
check(setup('alex', code2, 'alexpassword3')[0] == 400 and code_for('alex') is None, 'five wrong guesses kill the code')
post('/admin/users', {'action': 'reset-password', 'username': 'alex'}); setup('alex', code_for('alex'), 'alexpassword3')
s, loc = post('/admin/users', {'action': 'toggle-mentor', 'username': 'alex'}); check('now a mentor' in loc, 'make mentor')
check(req('/admin', 'alex', 'alexpassword3')[0] == 200, 'new mentor sees admin')
s, loc = post('/admin/users', {'action': 'delete-user', 'username': 'mentor1'}); check('own account' in loc, 'cannot delete self')
s, loc = post('/admin/users', {'action': 'delete-user', 'username': 'alex'}); check('Deleted alex' in loc, 'delete user')
check(req('/catalog.json', 'alex', 'alexpassword3')[0] == 401, 'deleted user rejected')
s, _, b = req('/admin', *M); check('Add bolt' in b and 'sarah' in b, 'activity shows submits')
check('<script' not in b, 'no scripts in page')
# Delete only empty, inactive seasons
s, loc = post('/admin', {'action': 'create-season', 'name': '2029-Robot'}); check('ok=Created' in loc, 'create 2029')
s, loc = post('/admin', {'action': 'delete-season', 'name': '2028-Robot'}); check('Make another season active' in loc or 'history' in loc, 'active/used season not deleted ' + loc)
s, loc = post('/admin', {'action': 'delete-season', 'name': '2029-Robot'}); check('ok=Deleted empty season' in loc, 'delete empty ' + loc)
s, _, b = req('/catalog.json', *U); check('2029-Robot' not in b, 'deleted season gone from catalog')
out = svn('svn ls http://localhost/svn/2029-Robot'); check(out.returncode != 0, 'deleted season not reachable')
s, loc = post('/admin', {'action': 'activate', 'name': '2027-Robot'}); check('ok=' in loc, 'reactivate 2027')
s, loc = post('/admin', {'action': 'delete-season', 'name': '2028-Robot'}); check('history' in loc, 'season with commits not deleted ' + loc)
# Add-in updates
import hashlib, json
def publish(name, data, required=False):
    s, _, b = req('/admin/addin', *M); tok = re.search(r'name="token" value="([0-9a-f]+)"', b).group(1)
    bnd = uuid.uuid4().hex
    body = (f'--{bnd}\r\nContent-Disposition: form-data; name="token"\r\n\r\n{tok}\r\n'
            f'--{bnd}\r\nContent-Disposition: form-data; name="kind"\r\n\r\naddin\r\n' +
            (f'--{bnd}\r\nContent-Disposition: form-data; name="required"\r\n\r\n1\r\n' if required else '') +
            f'--{bnd}\r\nContent-Disposition: form-data; name="files"; filename="{name}"\r\nContent-Type: application/octet-stream\r\n\r\n').encode() + data + f'\r\n--{bnd}--\r\n'.encode()
    s, h, _ = req('/admin/addin', *M, data=body, ctype='multipart/form-data; boundary=' + bnd)
    return urllib.parse.unquote(h['Location'])
exe = b'MZ' + bytes(range(256)) * 400
check(req('/admin/addin', *M)[0] == 200 and req('/admin/addin', *U)[0] == 403, 'add-in page mentor only')
check('Use the file made' in publish('evil.exe', exe), 'bad installer name refused')
check('not a Windows program' in publish('JOCO-ROBOS-CAD-Setup-0.6.0.exe', b'hello'), 'non-exe refused')
check('Published add-in 0.6.0' in publish('JOCO-ROBOS-CAD-Setup-0.6.0.exe', exe), 'publish 0.6.0')
catalog = json.loads(req('/catalog.json', *U)[2])
check(catalog['addin']['version'] == '0.6.0' and catalog['addin']['sha256'] == hashlib.sha256(exe).hexdigest() and not catalog['addin']['required'], 'catalog advertises update')
raw = urllib.request.Request(BASE + '/updates/JOCO-ROBOS-CAD-Setup-0.6.0.exe'); raw.add_header('Authorization', 'Basic ' + base64.b64encode(b'sarah:sarahpass123').decode())
check(hashlib.sha256(urllib.request.urlopen(raw).read()).hexdigest() == catalog['addin']['sha256'], 'downloaded bytes match checksum')
check(req('/updates/JOCO-ROBOS-CAD-Setup-0.6.0.exe')[0] == 401, 'anonymous download refused')
check('not newer' in publish('JOCO-ROBOS-CAD-Setup-0.5.9.exe', exe), 'older version refused')
check('required update' in publish('JOCO-ROBOS-CAD-Setup-0.10.0.exe', exe, required=True), 'publish required 0.10.0 (numeric compare)')
check(json.loads(req('/catalog.json', *U)[2])['addin']['required'], 'required in catalog')
s, loc = post('/admin/addin', {'action': 'addin-required'}); check('now optional' in loc, 'toggle required')
# GitHub staging account
P = ('github-release', 'releasepass123')
def stage(name, data, sha, user=P):
    r = urllib.request.Request(BASE + '/admin/api/stage-addin', data=data, method='POST')
    r.add_header('Authorization', 'Basic ' + base64.b64encode(f'{user[0]}:{user[1]}'.encode()).decode())
    r.add_header('X-Joco-File', name); r.add_header('X-Joco-Sha256', sha); r.add_header('Content-Type', 'application/octet-stream')
    try:
        resp = urllib.request.urlopen(r); return resp.status, resp.read().decode()
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode()
out = svn('svn ls http://localhost/svn/2027-Robot', user=P); check(out.returncode != 0, 'publisher has no SVN read access')
out = svn('svn mkdir -m x http://localhost/svn/Library/Evil', user=P); check(out.returncode != 0, 'publisher has no SVN write access')
check(req('/admin', *P)[0] == 403, 'publisher cannot open mentor pages')
out = svn('svn ls http://localhost/svn/Library'); check(out.returncode == 0, 'students still read SVN after authz change ' + out.stderr)
exe2 = b'MZ' + bytes(range(256)) * 500
check(stage('JOCO-ROBOS-CAD-Setup-0.11.0.exe', exe2, 'ab' * 32)[0] == 400, 'staging with wrong checksum refused')
check(stage('JOCO-ROBOS-CAD-Setup-0.11.0.exe', exe2[:1000], hashlib.sha256(exe2).hexdigest())[0] == 400, 'truncated installer refused')
check(stage('JOCO-ROBOS-CAD-Setup-0.11.0.exe', exe2, hashlib.sha256(exe2).hexdigest(), user=M)[0] == 403, 'mentor cannot use staging API')
status, text = stage('JOCO-ROBOS-CAD-Setup-0.11.0.exe', exe2, hashlib.sha256(exe2).hexdigest()); check(status == 200 and 'Staged 0.11.0' in text, 'stage ' + text)
check(json.loads(req('/catalog.json', *U)[2])['addin']['version'] == '0.10.0', 'staged installer not offered to students yet')
check('0.11.0' in req('/admin/addin', *M)[2], 'staged shown to mentors')
s, loc = post('/admin/addin', {'action': 'release-staged'}); check('Published add-in 0.11.0' in loc, 'release staged ' + loc)
cat = json.loads(req('/catalog.json', *U)[2])['addin']
check(cat['version'] == '0.11.0' and cat['sha256'] == hashlib.sha256(exe2).hexdigest(), 'released installer offered')
check('not newer' in stage('JOCO-ROBOS-CAD-Setup-0.11.0.exe', exe2, hashlib.sha256(exe2).hexdigest())[1], 'restaging same version refused')
s, loc = post('/admin/users', {'action': 'toggle-mentor', 'username': 'github-release'}); check('cannot be a mentor' in loc, 'publisher cannot become mentor')
# manual upload with expected checksum
def publish_sha(name, data, sha):
    s, _, b = req('/admin/addin', *M); tok = re.search(r'name="token" value="([0-9a-f]+)"', b).group(1)
    bnd = uuid.uuid4().hex
    body = (f'--{bnd}\r\nContent-Disposition: form-data; name="token"\r\n\r\n{tok}\r\n'
            f'--{bnd}\r\nContent-Disposition: form-data; name="kind"\r\n\r\naddin\r\n'
            f'--{bnd}\r\nContent-Disposition: form-data; name="sha256"\r\n\r\n{sha}\r\n'
            f'--{bnd}\r\nContent-Disposition: form-data; name="files"; filename="{name}"\r\nContent-Type: application/octet-stream\r\n\r\n').encode() + data + f'\r\n--{bnd}--\r\n'.encode()
    return urllib.parse.unquote(req('/admin/addin', *M, data=body, ctype='multipart/form-data; boundary=' + bnd)[1]['Location'])
check('probably incomplete' in publish_sha('JOCO-ROBOS-CAD-Setup-0.12.0.exe', exe2[:5000], hashlib.sha256(exe2).hexdigest()), 'manual truncated upload refused')
# Undo a submit (2027-Robot is active again here)
def sh(cmd):
    return subprocess.run(['docker', 'exec', 'joco-test', 'sh', '-c', cmd], capture_output=True, text=True)
AUTH = ' --non-interactive --no-auth-cache --username sarah --password sarahpass123'
sh('rm -rf /tmp/u && svn co -q http://localhost/svn/2027-Robot /tmp/u' + AUTH)
sh('cd /tmp/u && printf v1 > 10_Drivetrain/Gear.SLDPRT && svn add -q 10_Drivetrain/Gear.SLDPRT && svn ps -q svn:needs-lock "*" 10_Drivetrain/Gear.SLDPRT && svn ps -q svn:mime-type application/octet-stream 10_Drivetrain/Gear.SLDPRT && svn ci -q -m "Add gear"' + AUTH)
sh('cd /tmp/u && svn lock -q 10_Drivetrain/Gear.SLDPRT' + AUTH + ' && chmod u+w 10_Drivetrain/Gear.SLDPRT && printf v2-bad > 10_Drivetrain/Gear.SLDPRT && mkdir 10_Drivetrain/Junk && printf x > 10_Drivetrain/Junk/Bad.SLDPRT && svn add -q 10_Drivetrain/Junk && svn ps -q svn:needs-lock "*" 10_Drivetrain/Junk/Bad.SLDPRT && svn ps -q svn:mime-type application/octet-stream 10_Drivetrain/Junk/Bad.SLDPRT && svn ci -q -m "Terrible submit"' + AUTH)
bad = int(sh('svnlook youngest /var/lib/svn/2027-Robot').stdout)
s_, _, page = req(f'/admin/undo?repo=2027-Robot&rev={bad}', *M); check(s_ == 200 and 'Terrible submit' in page and 'will be removed' in page and 'previous version restored' in page, 'undo preview')
check('Undo…' in req('/admin', *M)[2], 'undo links on recent submits')
s_, loc = post(f'/admin/undo?repo=2027-Robot&rev={bad}', {'action': 'undo-submit', 'repo': '2027-Robot', 'rev': str(bad)}); check('ok=Undid r%d' % bad in loc, 'undo ' + loc)
check(sh('svnlook cat /var/lib/svn/2027-Robot 10_Drivetrain/Gear.SLDPRT').stdout == 'v1', 'undo restored previous content')
check(sh('svnlook tree /var/lib/svn/2027-Robot 10_Drivetrain/Junk').returncode != 0, 'undo removed added folder')
check(sh('svnlook propget /var/lib/svn/2027-Robot svn:needs-lock 10_Drivetrain/Gear.SLDPRT').stdout.strip() == '*', 'restored file keeps lock policy')
check(int(sh('svnlook youngest /var/lib/svn/2027-Robot').stdout) == bad + 1, 'undo is a new revision, history kept')
# refusals: file changed later, file locked, non-mentor revprop
sh('cd /tmp/u && svn up -q' + AUTH + ' && svn lock -q 10_Drivetrain/Gear.SLDPRT' + AUTH + ' && chmod u+w 10_Drivetrain/Gear.SLDPRT && printf v3 > 10_Drivetrain/Gear.SLDPRT && svn ci -q -m "Good change"' + AUTH)
s_, loc = post('/admin', {'action': 'undo-submit', 'repo': '2027-Robot', 'rev': str(bad)}); check('Later submits changed' in loc, 'undo refused when later changed ' + loc)
sh('cd /tmp/u && svn lock -q 10_Drivetrain/Gear.SLDPRT' + AUTH)
good = int(sh('svnlook youngest /var/lib/svn/2027-Robot').stdout)
s_, loc = post('/admin', {'action': 'undo-submit', 'repo': '2027-Robot', 'rev': str(good)}); check('locked by sarah' in loc, 'undo refused while a student holds the lock ' + loc)
out = sh('cd /tmp/u && chmod u+w 10_Drivetrain/Gear.SLDPRT && printf sneaky > 10_Drivetrain/Gear.SLDPRT && svn unlock -q 10_Drivetrain/Gear.SLDPRT' + AUTH + '; svn ci -m sneaky --with-revprop joco:mentor-undo=1' + AUTH)
check(out.returncode != 0, 'students cannot use the undo exception')
sh('cd /tmp/u && svn revert -q 10_Drivetrain/Gear.SLDPRT')
# Heartbeats
def beat(user, body, client='addin'):
    r = urllib.request.Request(BASE + '/admin/api/heartbeat', data=json.dumps(body).encode(), method='POST')
    r.add_header('Authorization', 'Basic ' + base64.b64encode(f'{user[0]}:{user[1]}'.encode()).decode())
    r.add_header('Content-Type', 'application/json')
    if client: r.add_header('X-Joco-Client', client)
    try:
        return urllib.request.urlopen(r).status
    except urllib.error.HTTPError as e:
        return e.code
check(beat(U, {'version': '0.11.0', 'computer': 'SARAH-LAPTOP'}) == 200, 'student heartbeat')
check(beat(U, {'version': '0.11.0'}, client=None) == 403, 'heartbeat needs add-in header')
check(beat(P, {'version': '0.11.0'}) == 403, 'publisher cannot heartbeat')
check(beat(U, {'version': '<script>'}) == 400, 'bad heartbeat version refused')
page = req('/admin/users', *M)[2]; check('0.11.0' in page and 'SARAH-LAPTOP' in page, 'accounts page shows add-in version and computer')
check('of 1 students' in req('/admin/addin', *M)[2], 'add-in adoption summary')
# Health
home = req('/admin', *M)[2]
check('Health' in home and 'Disk:' in home and 'Deck backup: no record yet' in home, 'health panel')
sh("mkdir -p /etc/joco/health && printf '{\"time\": \"%s\"}' $(date -u +%Y-%m-%dT%H:%M:%SZ) > /etc/joco/health/backup.json")
check('✓ Deck backup' in req('/admin', *M)[2], 'fresh backup shown healthy')
# Season-change warning with locks outstanding
sh('cd /tmp/u && svn up -q' + AUTH + ' && svn lock -q 10_Drivetrain/Gear.SLDPRT' + AUTH)
s_, loc = post('/admin', {'action': 'create-season', 'name': '2030-Robot'})
s_, loc = post('/admin', {'action': 'activate', 'name': '2030-Robot'}); check('still has 1 locked file' in loc and 'sarah' in loc, 'activate warns about outstanding locks ' + loc)
post('/admin', {'action': 'activate', 'name': '2027-Robot'})
# FRCDesignLib adapter with a seeded fake catalog (container runs with JOCO_FRC_CATALOG pointing nowhere)
catalog = {'groupOrder': ['g1', 'g2'], 'groups': {'g1': {'name': 'Bearings'}, 'g2': {'name': 'Motors & Servos'}}, 'insertables': {
    't-bearing': {'id': 't-bearing', 'name': 'Test Flanged Bearing', 'vendors': ['WCP'], 'groupId': 'g1', 'elementType': 'PARTSTUDIO',
                  'isConfigurable': False, 'isVisible': True, 'documentId': 'd1', 'versionId': 'v1', 'elementId': 'e1', 'microversionId': 'm1',
                  'largeThumbnailUrl': '/api/thumbnail/300x300/e1?v=m1', 'smallThumbnailUrl': '/api/thumbnail/70x40/e1?v=m1'},
    't-motor': {'id': 't-motor', 'name': 'Test Motor', 'vendors': ['CTRE'], 'groupId': 'g2', 'elementType': 'ASSEMBLY',
                'isConfigurable': True, 'isVisible': True, 'documentId': 'd2', 'versionId': 'v2', 'elementId': 'e2', 'microversionId': 'm2',
                'largeThumbnailUrl': '/api/thumbnail/300x300/e2?v=m2'}}}
config = {'parameters': [{'id': 'Cap', 'name': 'Back Cap', 'type': 'enum', 'default': 'Default', 'options': [{'id': 'Default', 'name': 'None'}, {'id': 'Std', 'name': 'Standard'}]},
                         {'id': 'Case', 'name': 'Include Case', 'type': 'boolean', 'default': 'true', 'condition': {'type': 'equal', 'id': 'Cap', 'value': 'Std'}}],
          'records': [{'partNumber': 'TM-1', 'configurationKey': ''}]}
seed = {'catalog.json': json.dumps(catalog), 'config-t-motor-m2.json': json.dumps(config), 'thumb-e1-m1-300x300': 'GIF89a-fake'}
for name, text in seed.items():
    subprocess.run(['docker', 'exec', '-i', 'joco-test', 'sh', '-c', f"mkdir -p /var/lib/svn/.frcdesign && cat > '/var/lib/svn/.frcdesign/{name}' && chown -R www-data:www-data /var/lib/svn/.frcdesign"], input=text.encode())
def frc(user, method, path, body=None, client='addin'):
    r = urllib.request.Request(BASE + '/admin/api/frcdesign/' + path, data=json.dumps(body).encode() if body is not None else None, method=method)
    r.add_header('Authorization', 'Basic ' + base64.b64encode(f'{user[0]}:{user[1]}'.encode()).decode())
    if client: r.add_header('X-Joco-Client', client)
    r.add_header('Content-Type', 'application/json')
    try:
        resp = urllib.request.urlopen(r); data = resp.read()
        return resp.status, (json.loads(data) if resp.headers.get('Content-Type') == 'application/json' else data)
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode(errors='replace')
check(frc(U, 'GET', 'search?q=bearing', client=None)[0] == 403, 'FRCDesignLib API needs the add-in header')
check(frc(P, 'GET', 'search?q=bearing')[0] == 403, 'publisher cannot use FRCDesignLib API')
s_, body = frc(U, 'GET', 'search?q=bearing'); check(s_ == 200 and [r['name'] for r in body['results']] == ['Test Flanged Bearing'], 'search ' + str(body))
s_, body = frc(U, 'GET', 'search?q=ctre'); check(s_ == 200 and body['results'][0]['kind'] == 'assembly', 'search by vendor')
s_, body = frc(U, 'GET', 'item/t-motor'); check(s_ == 200 and body['parameters'][0]['id'] == 'Cap' and body['partNumber'] == 'TM-1', 'item details')
check(body['choices'][1] == {'id': 'Case', 'name': 'Include Case', 'kind': 'boolean', 'default': 'true', 'options': [],
                             'visibleWhen': {'mode': 'equals', 'id': 'Cap', 'value': 'Std'}}, 'choices in JOCO shape ' + str(body['choices']))
check(frc(U, 'GET', 'item/../../etc/passwd')[0] in (400, 403, 404) and frc(U, 'GET', 'item/not-a-real-id')[0] == 404, 'unknown item refused')
s_, body = frc(U, 'GET', 'thumb/t-bearing'); check(s_ == 200 and body.startswith(b'GIF89a'), 'thumbnail proxied from cache')
s_, body = frc(U, 'POST', 'claim', {'id': 't-motor', 'configuration': {'Cap': 'Nope'}}); check(s_ == 400 and 'Invalid choice' in body, 'invalid configuration refused')
s_, body = frc(U, 'POST', 'claim', {'id': 't-motor', 'configuration': {'Cap': 'Std'}})
check(s_ == 200 and body['status'] == 'yours' and body['libraryPath'] == 'FRCDesignLib/Motors & Servos/Test Motor (Standard).SLDPRT', 'claim configured motor ' + str(body))
s_, body = frc(U, 'POST', 'claim', {'id': 't-bearing'}); check(s_ == 200 and body['status'] == 'yours' and body['libraryPath'] == 'FRCDesignLib/Bearings/Test Flanged Bearing.SLDPRT', 'claim bearing')
fp = body['fingerprint']
s_, body = frc(M, 'POST', 'claim', {'id': 't-bearing'}); check(s_ == 200 and body['status'] == 'busy' and body['by'] == 'sarah', 'second student sees busy')
s_, body = frc(M, 'GET', 'download/' + fp); check(s_ == 409, 'only the claimant can download')
s_, body = frc(U, 'GET', 'download/' + fp); check(s_ == 503 and 'not set up' in body, 'no Onshape key: clear message, no export')
s_, body = frc(U, 'POST', 'complete', {'fingerprint': fp}); check(s_ == 409 and 'Submit it first' in body, 'complete requires the Library file')
sh('rm -rf /tmp/lib2 && svn co -q http://localhost/svn/Library /tmp/lib2' + AUTH + ' && mkdir -p "/tmp/lib2/FRCDesignLib/Bearings" && printf part > "/tmp/lib2/FRCDesignLib/Bearings/Test Flanged Bearing.SLDPRT" && cd /tmp/lib2 && svn add -q --parents "FRCDesignLib/Bearings/Test Flanged Bearing.SLDPRT" && svn ps -q svn:needs-lock "*" "FRCDesignLib/Bearings/Test Flanged Bearing.SLDPRT" && svn ps -q svn:mime-type application/octet-stream "FRCDesignLib/Bearings/Test Flanged Bearing.SLDPRT" && svn ci -q -m "Import from FRCDesignLib"' + AUTH)
s_, body = frc(U, 'POST', 'complete', {'fingerprint': fp}); check(s_ == 200 and body['status'] == 'ready', 'complete after Library submit ' + str(body))
s_, body = frc(M, 'POST', 'claim', {'id': 't-bearing'}); check(s_ == 200 and body['status'] == 'ready' and body['libraryPath'].endswith('Test Flanged Bearing.SLDPRT'), 'next student reuses the Library copy')
page = req('/admin/library', *M)[2]; check('FRCDesignLib imports' in page and 'Test Flanged Bearing' in page and 'no Onshape key' in page, 'mentor sees FRCDesignLib imports')
s_, body = frc(U, 'POST', 'abandon', {'fingerprint': 'nothing'}); check(s_ == 200, 'abandon is harmless')
print(f'PASS: {n} server/admin checks')
