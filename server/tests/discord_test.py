# Pure checks of Discord sign-in (discord_auth.py) with Discord's API faked: no network, no container.
#   python3 server/tests/discord_test.py
import base64, contextlib, hashlib, os, secrets, sys, tempfile, time
from urllib.parse import parse_qs, urlsplit
sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..'))
os.environ['JOCO_CONFIG'] = tempfile.mkdtemp()
os.environ.update(DISCORD_CLIENT_ID='111', DISCORD_CLIENT_SECRET='shh', DISCORD_GUILD_ID='651908512842252290',
                  DISCORD_GUILD_NAME='JoCo RoBos', DISCORD_MENTOR_IDS='900', JOCO_SERVER_NAME='cad.example.org')
import discord_auth as d

n = 0
def check(condition, message):
    global n
    if not condition:
        raise SystemExit('FAIL: ' + message)
    n += 1

# ---- a fake Discord ----
people = {}   # access token -> (profile, [guild ids])
codes = {}    # code -> access token
refreshes = {}  # refresh token -> access token, or 'gone'
outage = [False]
calls = []
def fake_http(method, url, data=None, token=None):
    calls.append((method, url))
    if outage[0]:
        raise OSError('down')
    if url.endswith('/oauth2/token'):
        assert data['client_id'] == '111' and data['client_secret'] == 'shh'
        if data['grant_type'] == 'authorization_code':
            assert data['redirect_uri'] == 'https://cad.example.org/account/discord/callback'
            access = codes.pop(data['code'], None)
            return (200, {'access_token': access, 'refresh_token': 'r-' + access}) if access else (400, {'error': 'invalid_grant'})
        access = refreshes.get(data['refresh_token'])
        if access in (None, 'gone'):
            return 400, {'error': 'invalid_grant'}
        return 200, {'access_token': access, 'refresh_token': data['refresh_token'] + '+'}
    profile, guilds = people[token]
    if url.endswith('/users/@me'):
        return 200, profile
    if '/users/@me/guilds' in url:
        after = int(parse_qs(urlsplit(url).query)['after'][0])
        page = [{'id': str(g)} for g in sorted(guilds) if g > after][:200]
        return 200, page
    raise AssertionError(url)
d._http = fake_http

def person(discord_id, username, member=True, extra_guilds=0):
    access = 'a' + discord_id
    guilds = list(range(1, extra_guilds + 1)) + ([651908512842252290] if member else [])
    people[access] = ({'id': discord_id, 'username': username}, guilds)
    refreshes['r-' + access] = access
    return access

# ---- what joco.py provides ----
htpasswd = {'imdad': 'old', 'lucas': 'old', 'github-release': 'gh'}
mentors = {'imdad'}
accounts = {'users': lambda: set(htpasswd), 'set_password': lambda u, p: htpasswd.__setitem__(u, p),
            'add_mentor': mentors.add, 'mentors': lambda: set(mentors), 'locked': contextlib.nullcontext}

def verifier_pair():
    v = secrets.token_urlsafe(32)
    return v, base64.urlsafe_b64encode(hashlib.sha256(v.encode()).digest()).rstrip(b'=').decode()

def sign_in(discord_id, username, kind='addin', member=True, extra_guilds=0):
    access = person(discord_id, username, member, extra_guilds)
    v, c = verifier_pair()
    state, url = d.start(kind, c if kind == 'addin' else None)
    code = secrets.token_hex(4)
    codes[code] = access
    try:
        result = d.finish(state, code, '', accounts)
    except d.DiscordError as exc:
        result = exc
    return v, url, result

# Starting: the add-in must bring a challenge; the URL asks Discord for identify + guilds, back to this server.
try:
    d.start('addin', 'short'); check(False, 'add-in start needs a challenge')
except d.DiscordError:
    check(True, '')
v, c = verifier_pair()
state, url = d.start('addin', c)
q = parse_qs(urlsplit(url).query)
check(url.startswith('https://discord.com/oauth2/authorize?') and q['client_id'] == ['111'] and q['scope'] == ['identify guilds']
      and q['redirect_uri'] == ['https://cad.example.org/account/discord/callback'] and q['state'] == [state], 'authorize URL')
check(d.poll(v) == ('pending',), 'pending before Discord answers')
check(d.poll('nonsense') == ('pending',) and d.poll('') == ('unknown',), 'nothing known yet is still pending (the browser may not have opened)')

# The add-in asks while the server is still checking with Discord (1.16.0 showed "expired" here): still pending, then done.
asked = []
race_v, race_c = verifier_pair()
real_http = d._http
def asking_http(method, url, data=None, token=None):
    if url.endswith('/users/@me'):
        asked.append(d.poll(race_v))
    return real_http(method, url, data, token)
codes['race'] = person('510', 'racer')
race_state, _ = d.start('addin', race_c)
d._http = asking_http
d.finish(race_state, 'race', '', accounts)
d._http = real_http
check(asked == [('pending',)] and d.poll(race_v)[0] == 'done', 'asked mid-sign-in: pending, then done ' + str(asked))
try:
    d.finish(race_state, 'race', '', accounts); check(False, 'a used sign-in can\'t finish twice')
except d.DiscordError:
    check(race_state not in d._flows, 'used sign-in gone')

# A new member: account named after their Discord username, key set as the SVN password, collected once.
v, _, (kind, user, _) = sign_in('501', 'sarah.k')
check(kind == 'addin' and user == 'sarah.k' and 'sarah.k' in htpasswd, 'new account from the Discord username')
r = d.poll(v)
check(r[0] == 'done' and r[1] == 'sarah.k' and r[2] == htpasswd['sarah.k'] and len(r[2]) == 43, 'add-in collects username and key')
check(d.poll(v) == ('pending',), 'collected only once')
key1 = r[2]

# The same person on another computer: the same account and the same key (the first computer keeps working).
v, _, (_, user, _) = sign_in('501', 'sarah.k')
check(user == 'sarah.k' and d.poll(v)[2] == key1, 'second computer gets the same key')
# A Discord rename doesn't rename the CAD account (locks and history are by username).
v, _, (_, user, _) = sign_in('501', 'sarah.renamed')
check(user == 'sarah.k' and d.load()['accounts']['501']['discord'] == 'sarah.renamed', 'renamed on Discord: same CAD account')
d.poll(v)

# Not in the team's server: refused, and the add-in hears why.
v, _, result = sign_in('502', 'stranger', member=False)
check(isinstance(result, d.DiscordError) and 'JoCo RoBos' in str(result) and 'stranger' not in htpasswd, 'non-member refused')
r = d.poll(v)
check(r[0] == 'error' and 'JoCo RoBos' in r[1], 'add-in told why')
# Membership found past the first page of servers.
v, _, (_, user, _) = sign_in('503', 'busy', extra_guilds=450)
check(user == 'busy', 'member found on a later page of servers'); d.poll(v)

# Existing password accounts: a student's own name is taken over (old password gone, same history); a mentor's only by a
# Discord id in DISCORD_MENTOR_IDS; github-release never.
v, _, (_, user, _) = sign_in('504', 'lucas')
check(user == 'lucas' and htpasswd['lucas'] != 'old', 'student password account taken over'); d.poll(v)
v, _, (_, user, _) = sign_in('505', 'imdad')
check(user == 'imdad-2' and htpasswd['imdad'] == 'old' and 'imdad-2' not in mentors, 'look-alike can\'t take a mentor account'); d.poll(v)
v, _, (_, user, _) = sign_in('900', 'imdad')
check(user == 'imdad' and htpasswd['imdad'] != 'old' and 'imdad' in mentors, 'the listed mentor takes over their account'); d.poll(v)
v, _, (_, user, _) = sign_in('506', 'github-release')
check(user == 'github-release-2' and htpasswd['github-release'] == 'gh', 'release account never taken'); d.poll(v)
v, _, (_, user, _) = sign_in('901', 'lucas')
check(user == 'lucas-2', 'a linked name is never shared'); d.poll(v)
# Discord usernames that aren't valid CAD usernames.
check(d.username_for({'id': '1', 'username': '_x.y'}, set()) == 'x.y', 'leading underscore dropped')
check(d.username_for({'id': '123456789', 'username': '__'}, set()) == 'student456789', 'too short')
check(d.username_for({'id': '1', 'username': 'Sam'}, {'sam', 'sam-2'}) == 'sam-3', 'numbered when taken')

# Mentor page sessions: signed, expire, end with "sign out everywhere".
v, _, (kind, user, _) = sign_in('501', 'sarah.k', kind='web')
check(kind == 'web' and d.poll(v) == ('pending',), 'web sign-in leaves nothing for an add-in to collect')
cookie = d.make_session('sarah.k')
check(d.read_session(cookie) == 'sarah.k', 'session read back')
check(d.read_session(cookie[:-2] + ('AA' if not cookie.endswith('AA') else 'BB')) is None, 'tampered session refused')
check(d.read_session(cookie, now=time.time() + d.SESSION_SECONDS + 5) is None, 'expired session refused')
check(d.read_session(d.make_session('lucas-zzz')) is None, 'session for an account that isn\'t Discord\'s refused')
check(d.sign_out_everywhere('sarah.k', accounts) and d.read_session(cookie) is None, 'sign out everywhere ends the session')
check(htpasswd['sarah.k'] != key1, 'sign out everywhere changes the key')
v, _, _ = sign_in('501', 'sarah.k')
key2 = d.poll(v)[2]
check(key2 != key1 and key2 == htpasswd['sarah.k'], 'signing in again gives a new key')
check(not d.sign_out_everywhere('github-release', accounts), 'only Discord accounts')

# Expired or reused sign-ins.
v, c = verifier_pair()
state, _ = d.start('addin', c)
d._flows[state]['created'] -= d.FLOW_SECONDS + 1
try:
    d.finish(state, 'x', '', accounts); check(False, 'expired flow')
except d.DiscordError:
    check(True, '')
try:
    d.finish('made-up', 'x', '', accounts); check(False, 'unknown state')
except d.DiscordError:
    check(True, '')
v, c = verifier_pair()
state, _ = d.start('addin', c)
try:
    d.finish(state, '', 'access_denied', accounts)
except d.DiscordError as exc:
    check('cancelled' in str(exc) and d.poll(v) == ('error', str(exc)), 'cancelled in Discord: the add-in hears it')
outage[0] = True
v, c = verifier_pair()
state, _ = d.start('addin', c)
codes['down'] = 'a501'
try:
    d.finish(state, 'down', '', accounts)
except d.DiscordError as exc:
    check('reach Discord' in str(exc) and d.poll(v)[0] == 'error', 'Discord down: told, nothing changed')
outage[0] = False

# The daily recheck: still a member (refresh token rotated), left the server (signed out, flagged), disconnected the app
# (signed out), Discord down (nobody removed).
data = d.load()
for a in data['accounts'].values():
    a['checked'] = 0
d.save(data)
people['a504'] = (people['a504'][0], [])           # lucas left the server
refreshes['r-a503'] = 'gone'                       # busy disconnected CAD Hub in Discord
outage[0] = True
check(d.recheck(accounts) == [], 'outage removes nobody')
outage[0] = False
removed = d.recheck(accounts)
check(sorted(removed) == ['busy', 'lucas'], 'left or disconnected: removed ' + str(removed))
check(d.load()['accounts']['504']['removed'] and 'refresh' not in d.load()['accounts']['504'], 'flagged, refresh token dropped')
check(d.load()['accounts']['501']['refresh'].endswith('+') and not d.load()['accounts']['501']['removed'], 'member kept, token rotated')
before = len(calls)
check(d.recheck(accounts) == [] and len(calls) == before, 'checked accounts wait a day')
# Back in the server: signing in again works.
people['a504'] = (people['a504'][0], [651908512842252290])
v, _, (_, user, _) = sign_in('504', 'lucas')
check(user == 'lucas' and not d.load()['accounts']['504']['removed'], 'rejoined: same account back'); d.poll(v)

# Not configured: refuses clearly.
os.environ['DISCORD_CLIENT_SECRET'] = ''
try:
    d.start('web'); check(False, 'unconfigured')
except d.DiscordError as exc:
    check('isn\'t set up' in str(exc), 'unconfigured server says so')
os.environ['DISCORD_CLIENT_SECRET'] = 'shh'
check(oct(os.stat(d.ACCOUNTS).st_mode)[-3:] in ('600', '666'), 'accounts file private')  # 666 on Windows test runs.

print('%d Discord sign-in checks passed' % n)
