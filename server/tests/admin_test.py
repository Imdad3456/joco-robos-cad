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
# Accounts
s, loc = post('/admin/users', {'action': 'add-user', 'username': 'alex', 'password': 'short'}); check('at least 10' in loc, 'short password refused')
s, loc = post('/admin/users', {'action': 'add-user', 'username': 'alex', 'password': 'alexpassword1'}); check('ok=Added alex' in loc, 'add user')
check(req('/catalog.json', 'alex', 'alexpassword1')[0] == 200, 'new user can sign in')
s, loc = post('/admin/users', {'action': 'reset-password', 'username': 'alex', 'password': 'alexpassword2'}); check('ok=Reset' in loc, 'reset')
check(req('/catalog.json', 'alex', 'alexpassword1')[0] == 401 and req('/catalog.json', 'alex', 'alexpassword2')[0] == 200, 'reset takes effect')
s, loc = post('/admin/users', {'action': 'toggle-mentor', 'username': 'alex'}); check('now a mentor' in loc, 'make mentor')
check(req('/admin', 'alex', 'alexpassword2')[0] == 200, 'new mentor sees admin')
s, loc = post('/admin/users', {'action': 'delete-user', 'username': 'mentor1'}); check('own account' in loc, 'cannot delete self')
s, loc = post('/admin/users', {'action': 'delete-user', 'username': 'alex'}); check('Deleted alex' in loc, 'delete user')
check(req('/catalog.json', 'alex', 'alexpassword2')[0] == 401, 'deleted user rejected')
s, _, b = req('/admin', *M); check('Add bolt' in b and 'sarah' in b, 'activity shows submits')
check('<script' not in b, 'no scripts in page')
print(f'PASS: {n} server/admin checks')
