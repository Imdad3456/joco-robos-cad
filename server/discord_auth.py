"""Discord sign-in for CAD Hub: members of the team's Discord server get a CAD account named after their Discord username.

SVN only understands a username and password on every request, so Discord can't be used directly. Instead, signing in with
Discord gives the student's add-in a long random key that works as that account's SVN password (Apache keeps only its bcrypt
hash in the users file, like any password). The key is derived from a server secret, the Discord id and a generation number:
signing in again on another computer gives the same key, and "sign out everywhere" (or leaving the Discord server) bumps the
generation so every old key stops working.

Two flows share Discord's authorize page:
  - addin: the add-in opens the browser at /account/discord/start?kind=addin&challenge=C, where C = base64url(sha256(V)) for a
    random verifier V only the add-in knows. After Discord, the add-in collects the username and key once by POSTing V to
    /account/discord/poll. Nobody else can collect it: the verifier never travels through the browser.
  - web: the mentor page. The browser carries a short-lived cookie with the flow's state; after Discord it gets a signed
    session cookie for /admin.

Mentor access is still the state.json mentor list (by CAD username). DISCORD_MENTOR_IDS bootstraps it: those Discord accounts
become mentors when they first sign in.

Membership is checked at every sign-in and again daily with the refresh token Discord gave at sign-in (the site never gets
the bot's token). Someone who left the Discord server loses access; Discord being down never removes anyone.
"""
import base64
import hashlib
import hmac
import json
import os
import re
import secrets
import tempfile
import threading
import time
import urllib.error
import urllib.parse
import urllib.request

CONFIG = os.environ.get('JOCO_CONFIG', '/etc/joco')
ACCOUNTS = os.path.join(CONFIG, 'discord.json')
SECRET = os.path.join(CONFIG, 'discord-secret')
API = 'https://discord.com/api/v10'
AUTHORIZE = 'https://discord.com/oauth2/authorize'
USER_AGENT = 'CADHub (https://github.com/Imdad3456/joco-robos-cad, 1)'
FLOW_SECONDS = 600
SESSION_SECONDS = 7 * 86400
RECHECK_SECONDS = 86400
MAX_FLOWS = 500
USERNAME = re.compile(r'^[a-z0-9][a-z0-9._-]{1,31}$')
CHALLENGE = re.compile(r'^[A-Za-z0-9_-]{43}$')

_lock = threading.Lock()
_flows = {}    # state -> {'kind', 'challenge', 'created'}
_results = {}  # challenge -> {'at', 'user', 'key'} or {'at', 'error'}


class DiscordError(Exception):
    """Shown to the person signing in."""


# ---------- configuration ----------

def settings():
    server = os.environ.get('JOCO_SERVER_NAME', 'localhost')
    return {
        'client_id': os.environ.get('DISCORD_CLIENT_ID', '').strip(),
        'client_secret': os.environ.get('DISCORD_CLIENT_SECRET', '').strip(),
        'guild': os.environ.get('DISCORD_GUILD_ID', '').strip(),
        'guild_name': os.environ.get('DISCORD_GUILD_NAME', '').strip() or 'the team\'s Discord server',
        'mentors': {m.strip() for m in os.environ.get('DISCORD_MENTOR_IDS', '').split(',') if m.strip()},
        'public': (os.environ.get('JOCO_PUBLIC_URL') or 'https://' + server).rstrip('/'),
    }


def configured():
    s = settings()
    return bool(s['client_id'] and s['client_secret'] and s['guild'])


def redirect_uri():
    return settings()['public'] + '/account/discord/callback'


def _b64(data):
    return base64.urlsafe_b64encode(data).rstrip(b'=').decode()


def _secret():
    try:
        with open(SECRET) as handle:
            value = handle.read().strip()
        if len(value) >= 32:
            return value.encode()
    except FileNotFoundError:
        pass
    value = secrets.token_hex(32)
    _write(SECRET, value + '\n')
    return value.encode()


def _write(path, text):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    fd, temp = tempfile.mkstemp(dir=os.path.dirname(path))
    with os.fdopen(fd, 'w') as handle:
        handle.write(text)
    os.chmod(temp, 0o600)
    os.replace(temp, path)


def load():
    try:
        with open(ACCOUNTS) as handle:
            data = json.load(handle)
    except (FileNotFoundError, ValueError):
        data = {}
    data.setdefault('accounts', {})
    return data


def save(data):
    _write(ACCOUNTS, json.dumps(data, indent=2, sort_keys=True) + '\n')


def key_for(discord_id, generation):
    """The SVN password for this Discord account: the same on every computer until the generation changes."""
    return _b64(hmac.new(_secret(), ('cadhub-key:%s:%d' % (discord_id, generation)).encode(), hashlib.sha256).digest())


def linked_users():
    """CAD username -> Discord id, for accounts that sign in with Discord."""
    return {a['user']: i for i, a in load()['accounts'].items()}


# ---------- Discord's API ----------

def _http(method, url, data=None, token=None):
    """(status, parsed JSON or {}). Network failures raise OSError."""
    headers = {'User-Agent': USER_AGENT, 'Accept': 'application/json'}
    body = None
    if data is not None:
        body = urllib.parse.urlencode(data).encode()
        headers['Content-Type'] = 'application/x-www-form-urlencoded'
    if token:
        headers['Authorization'] = 'Bearer ' + token
    request = urllib.request.Request(url, data=body, method=method, headers=headers)
    try:
        with urllib.request.urlopen(request, timeout=20) as response:
            return response.status, json.loads(response.read() or b'{}')
    except urllib.error.HTTPError as exc:
        try:
            return exc.code, json.loads(exc.read() or b'{}')
        except ValueError:
            return exc.code, {}


def _token(grant):
    s = settings()
    status, body = _http('POST', API + '/oauth2/token', dict(grant, client_id=s['client_id'], client_secret=s['client_secret']))
    return status, body


def _profile(access):
    status, me = _http('GET', API + '/users/@me', token=access)
    if status != 200 or not me.get('id') or not me.get('username'):
        raise DiscordError('Discord didn\'t share your account. Try again.')
    return me


def _member(access):
    """True/False, or None when Discord couldn't say (outage, rate limit)."""
    guild, after = settings()['guild'], '0'
    for _ in range(10):  # Up to 2,000 servers.
        status, page = _http('GET', API + '/users/@me/guilds?limit=200&after=' + after, token=access)
        if status != 200 or not isinstance(page, list):
            return None
        if any(str(g.get('id')) == guild for g in page):
            return True
        if len(page) < 200:
            return False
        after = str(page[-1].get('id'))
    return False


# ---------- sign-in ----------

def _prune(now):
    for state in [k for k, v in _flows.items() if now - v['created'] > FLOW_SECONDS]:
        del _flows[state]
    for challenge in [k for k, v in _results.items() if now - v['at'] > FLOW_SECONDS]:
        del _results[challenge]


def start(kind, challenge=None):
    """Begins a sign-in: returns (state, Discord's authorize URL)."""
    if not configured():
        raise DiscordError('Discord sign-in isn\'t set up on this server yet. Ask a mentor.')
    if kind not in ('addin', 'web'):
        raise DiscordError('Unknown sign-in.')
    if kind == 'addin' and not CHALLENGE.match(challenge or ''):
        raise DiscordError('Start signing in from SOLIDWORKS (Tools → CAD Hub → Sign In).')
    now = time.time()
    with _lock:
        _prune(now)
        if len(_flows) >= MAX_FLOWS:
            raise DiscordError('Too many sign-ins at once. Try again in a few minutes.')
        state = secrets.token_urlsafe(24)
        _flows[state] = {'kind': kind, 'challenge': challenge if kind == 'addin' else None, 'created': now}
    query = urllib.parse.urlencode({'client_id': settings()['client_id'], 'response_type': 'code', 'redirect_uri': redirect_uri(),
                                    'scope': 'identify guilds', 'state': state, 'prompt': 'none'})
    return state, AUTHORIZE + '?' + query


def flow(state):
    """The pending flow for a state, without using it up (None if unknown or expired)."""
    with _lock:
        f = _flows.get(state or '')
        return dict(f) if f and time.time() - f['created'] <= FLOW_SECONDS else None


def finish(state, code, error, accounts):
    """Discord sent the browser back. Returns (kind, username, discord account). For the add-in flow the result also waits
    for the add-in's poll; errors are recorded for it too, then raised.

    accounts: what joco.py provides, called under its lock: users() -> set of CAD usernames, set_password(user, secret),
    add_mentor(user), mentors() -> set of mentor usernames, locked() -> context manager."""
    with _lock:
        f = _flows.get(state or '')
        if not f or f.get('used') or time.time() - f['created'] > FLOW_SECONDS:
            raise DiscordError('That sign-in expired. Start again from SOLIDWORKS or the mentor page.')
        # Used up now, but kept until its result is stored: the add-in may ask while Discord is still being checked.
        f['used'] = True
    try:
        return _finish(f, code, error, accounts)
    finally:
        with _lock:
            _flows.pop(state, None)


def _finish(f, code, error, accounts):
    try:
        if error:
            raise DiscordError('Sign-in was cancelled in Discord.' if error == 'access_denied' else 'Discord refused the sign-in (%s).' % error[:60])
        status, grant = _token({'grant_type': 'authorization_code', 'code': code or '', 'redirect_uri': redirect_uri()})
        if status != 200 or not grant.get('access_token'):
            raise DiscordError('Discord didn\'t accept the sign-in. Try again.')
        profile = _profile(grant['access_token'])
        member = _member(grant['access_token'])
        if member is None:
            raise DiscordError('Discord didn\'t answer. Try again in a minute.')
        if not member:
            raise DiscordError('%s isn\'t in %s. Join it (ask a mentor for an invite), then sign in again.' % (
                profile['username'], settings()['guild_name']))
        with accounts['locked']():
            user, key = link(profile, grant.get('refresh_token'), accounts)
    except DiscordError as exc:
        if f['kind'] == 'addin':
            with _lock:
                _results[f['challenge']] = {'at': time.time(), 'error': str(exc)}
        raise
    except OSError:
        message = 'Couldn\'t reach Discord. Try again in a minute.'
        if f['kind'] == 'addin':
            with _lock:
                _results[f['challenge']] = {'at': time.time(), 'error': message}
        raise DiscordError(message)
    if f['kind'] == 'addin':
        with _lock:
            _results[f['challenge']] = {'at': time.time(), 'user': user, 'key': key}
    return f['kind'], user, profile


def poll(verifier):
    """The add-in collects its sign-in: ('done', user, key), ('error', message), ('pending',), or ('unknown',) for a malformed
    verifier. Nothing known yet is still 'pending': the add-in starts asking as it opens the browser, often before the browser
    reaches the start page, and it gives up by itself after 10 minutes. (1.16.0 treats 'unknown' as expired.)"""
    if not verifier or len(verifier) > 200:
        return ('unknown',)
    challenge = _b64(hashlib.sha256(verifier.encode()).digest())
    with _lock:
        _prune(time.time())
        result = _results.pop(challenge, None)
        if result is None:
            return ('pending',)
    if 'error' in result:
        return ('error', result['error'])
    return ('done', result['user'], result['key'])


def username_for(profile, taken):
    """The Discord username, made into a valid CAD username that nobody else has."""
    base = re.sub(r'[^a-z0-9._-]', '', str(profile.get('username', '')).lower()).lstrip('._-')[:32]
    if len(base) < 2:
        base = 'student' + str(profile.get('id', ''))[-6:]
    name, n = base, 2
    while name in taken or not USERNAME.match(name):
        name = base[:28].rstrip('._-') + '-' + str(n)
        n += 1
    return name


def link(profile, refresh, accounts):
    """Finds or creates the CAD account for a Discord member; sets its key. Call under joco.py's lock. Returns (user, key).

    A new member gets their Discord username. If a password account already has that name, it becomes theirs (its old
    password stops working; history and locks stay), except a mentor's: only a Discord id in DISCORD_MENTOR_IDS can take over
    a mentor account, so a look-alike name can't gain mentor rights. Anyone else with a taken name gets name-2."""
    data = load()
    discord_id = str(profile['id'])
    account = data['accounts'].get(discord_id)
    now = int(time.time())
    mentor_ids = settings()['mentors']
    if account is None:
        linked = {a['user'] for a in data['accounts'].values()}
        mentors = accounts['mentors']()
        existing = accounts['users']()
        wanted = username_for(profile, linked | {'github-release'})
        takeover = wanted in existing and wanted not in linked and (wanted not in mentors or discord_id in mentor_ids)
        taken = linked | {'github-release'} | (set(existing) - ({wanted} if takeover else set()))
        account = {'user': wanted if takeover else username_for(profile, taken), 'generation': 1, 'since': now}
        data['accounts'][discord_id] = account
    account.update({'discord': str(profile.get('username', ''))[:40], 'checked': now, 'removed': False})
    if refresh:
        account['refresh'] = refresh
    key = key_for(discord_id, account['generation'])
    accounts['set_password'](account['user'], key)
    save(data)
    if discord_id in mentor_ids:
        accounts['add_mentor'](account['user'])
    return account['user'], key


def sign_out_everywhere(user, accounts, removed=False):
    """Every computer's key for this account stops working; signing in with Discord again gives a new one. Under the lock."""
    data = load()
    for discord_id, account in data['accounts'].items():
        if account['user'] == user:
            account['generation'] += 1
            account['removed'] = removed
            if removed:
                account.pop('refresh', None)
            accounts['set_password'](user, secrets.token_urlsafe(32))
            save(data)
            return True
    return False


def forget(user):
    """The account was deleted: its Discord link goes too (signing in again makes a new account). Under the lock."""
    data = load()
    data['accounts'] = {i: a for i, a in data['accounts'].items() if a['user'] != user}
    save(data)


def recheck(accounts, now=None):
    """Daily: is everyone still in the Discord server? Removes access for anyone who left or disconnected CAD Hub in Discord.
    Never for an outage. Returns the usernames removed."""
    now = now or time.time()
    removed = []
    due = [(i, a) for i, a in load()['accounts'].items() if not a.get('removed') and a.get('refresh') and now - a.get('checked', 0) > RECHECK_SECONDS]
    for discord_id, account in due:
        try:
            status, grant = _token({'grant_type': 'refresh_token', 'refresh_token': account['refresh']})
            if status == 400 and grant.get('error') == 'invalid_grant':
                member = False  # Disconnected in Discord's settings (or the grant expired): sign in again to continue.
            elif status != 200 or not grant.get('access_token'):
                continue
            else:
                member = _member(grant['access_token'])
        except OSError:
            continue
        if member is None:
            continue
        with accounts['locked']():
            data = load()
            current = data['accounts'].get(discord_id)
            if not current or current.get('checked', 0) != account.get('checked', 0):
                continue  # Signed in meanwhile.
            if member:
                current['checked'] = int(now)
                if grant.get('refresh_token'):
                    current['refresh'] = grant['refresh_token']
                save(data)
            else:
                sign_out_everywhere(current['user'], accounts, removed=True)
                removed.append(current['user'])
    return removed


# ---------- mentor page sessions ----------

def make_session(user, now=None):
    """A signed cookie value for /admin, valid for a week or until the account signs out everywhere."""
    data = load()
    account = next((a for a in data['accounts'].values() if a['user'] == user), None)
    payload = _b64(json.dumps({'u': user, 'g': account['generation'] if account else 0, 'e': int((now or time.time()) + SESSION_SECONDS)}).encode())
    return payload + '.' + _b64(hmac.new(_secret(), ('session:' + payload).encode(), hashlib.sha256).digest())


def read_session(value, now=None):
    """The CAD username a session cookie belongs to, or None."""
    payload, _, signature = (value or '').partition('.')
    if not payload or not signature:
        return None
    expected = _b64(hmac.new(_secret(), ('session:' + payload).encode(), hashlib.sha256).digest())
    if not hmac.compare_digest(signature, expected):
        return None
    try:
        session = json.loads(base64.urlsafe_b64decode(payload + '=' * (-len(payload) % 4)))
    except ValueError:
        return None
    if session.get('e', 0) < (now or time.time()):
        return None
    account = next((a for a in load()['accounts'].values() if a['user'] == session.get('u')), None)
    if not account or account.get('removed') or account['generation'] != session.get('g'):
        return None
    return session['u']
