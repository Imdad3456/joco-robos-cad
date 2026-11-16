# Disposable-container test of Discord sign-in through Apache: who can reach what, with a Discord session, a Discord key and a
# password account. Discord itself isn't contacted: accounts are linked inside the container the way a finished sign-in does.
# Needs a test container named joco-discord-test on 127.0.0.1:18191 (see the end of server/README.md). Never run against the Deck's
# real service.
import base64, json, re, subprocess, sys, urllib.error, urllib.parse, urllib.request
BASE = 'http://127.0.0.1:18191'
NAME = 'joco-discord-test'
n = 0


def check(condition, message):
    global n
    if not condition:
        raise SystemExit('FAIL: ' + message)
    n += 1


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *a):
        return None


def req(path, user=None, pw=None, data=None, headers=None, origin=True):
    r = urllib.request.Request(BASE + path, data=data)
    if user:
        r.add_header('Authorization', 'Basic ' + base64.b64encode(('%s:%s' % (user, pw)).encode()).decode())
    if data is not None and origin:
        r.add_header('Origin', BASE)
    for k, v in (headers or {}).items():
        r.add_header(k, v)
    try:
        resp = urllib.request.build_opener(NoRedirect).open(r)
        return resp.status, resp.headers, resp.read().decode()
    except urllib.error.HTTPError as e:
        return e.code, e.headers, e.read().decode(errors='replace')


def inside(code):
    """Python inside the container, as the admin service's user, with joco.py importable."""
    result = subprocess.run(['podman', 'exec', '-i', NAME, 'runuser', '-u', 'www-data', '--', 'env', 'PYTHONPATH=/opt/joco',
                             'DISCORD_CLIENT_ID=1', 'DISCORD_CLIENT_SECRET=x', 'DISCORD_GUILD_ID=2', 'DISCORD_MENTOR_IDS=900',
                             'python3', '-'], input=code, capture_output=True, text=True)
    if result.returncode:
        raise SystemExit('FAIL inside the container: ' + result.stderr)
    return result.stdout.strip()


LINK = '''
import joco, discord_auth as d
with joco.Locked():
    print(d.link({'id': '%s', 'username': '%s'}, 'refresh', joco.DISCORD_ACCOUNTS)[1])
'''

# Anonymous: the mentor page offers Discord sign-in; everything else still needs a username and password.
s, h, b = req('/admin')
check(s == 200 and 'Sign in with Discord' in b and '/account/discord/start?kind=web' in b and 'Seasons' not in b, 'anonymous mentor page: sign-in only')
check('WWW-Authenticate' not in h, 'no browser password box on the mentor page')
s, h, _ = req('/catalog.json')
check(s == 401 and 'JOCO ROBOS CAD' in h.get('WWW-Authenticate', ''), 'catalog still needs a sign-in (and add-ins still recognize the server)')
check(req('/svn/')[0] in (401, 403), 'SVN still needs a sign-in')
check(req('/admin/api/heartbeat', data=b'{}', headers={'X-Joco-Client': 'addin'})[0] == 403, 'add-in API refuses anonymous')
check(req('/admin', headers={'X-Joco-User': 'mentor1'})[2].count('Sign in with Discord') == 1, 'a forged username header is ignored')
s, _, _ = req('/admin/users', data=b'action=toggle-mentor&username=sarah&token=x', headers={'X-Joco-User': 'mentor1'})
check(s == 200, 'anonymous post just gets the sign-in page')

# Starting Discord sign-in.
s, h, _ = req('/account/discord/start?kind=addin&challenge=' + 'A' * 43)
loc = h.get('Location', '')
check(s == 303 and loc.startswith('https://discord.com/oauth2/authorize?') and 'redirect_uri=https%3A%2F%2Flocalhost%2Faccount%2Fdiscord%2Fcallback' in loc,
      'add-in sign-in goes to Discord ' + loc)
s, h, _ = req('/account/discord/start?kind=web')
check(s == 303 and 'cadhub_signin=' in h.get('Set-Cookie', '') and 'Path=/account/discord' in h.get('Set-Cookie', ''), 'web sign-in ties itself to the browser')
check(req('/account/discord/start?kind=addin&challenge=short')[0] == 400, 'add-in sign-in needs a challenge')
s, _, b = req('/account/discord/poll', data=json.dumps({'verifier': 'nope'}).encode())
check(s == 200 and json.loads(b)['status'] == 'pending', 'poll before the browser starts: pending')
s, _, b = req('/account/discord/callback?state=made-up&code=x')
check(s == 403 and 'expired' in b, 'unknown sign-in refused')
s, _, b = req('/account/setup', data=b'{}')
check(s == 410 and 'Discord' in b, 'old setup codes point to Discord')
check(req('/account/request', data=b'{}')[0] == 410, 'old account requests point to Discord')

# A Discord member's key works for SVN and the add-in APIs like a password; the mentor list decides the mentor page.
key = inside(LINK % ('501', 'sarah.k'))
check(len(key) == 43, 'key made')
check(req('/catalog.json', 'sarah.k', key)[0] == 200, 'Discord key reads the catalog')
check(req('/catalog.json', 'sarah.k', key[:-1] + 'x')[0] == 401, 'a wrong key is refused')
check(req('/admin/api/heartbeat', 'sarah.k', key, data=b'{"version": "1.16.0"}', headers={'X-Joco-Client': 'addin'})[0] == 200, 'Discord key works for the add-in APIs')
check(req('/admin', 'sarah.k', key)[0] == 403, 'a student isn\'t a mentor')
r = subprocess.run(['podman', 'exec', NAME, 'svn', 'ls', '--non-interactive', '--no-auth-cache', '--username', 'sarah.k', '--password', key,
                    'http://127.0.0.1/svn/2027-Robot/'], capture_output=True, text=True)
check(r.returncode == 0 and '00_Master' in r.stdout, 'Discord key works for SVN itself: ' + r.stderr)

# The mentor (DISCORD_MENTOR_IDS) takes over the existing password account of the same name and gets a session cookie.
key_m = inside(LINK % ('900', 'mentor1'))
check(req('/catalog.json', 'mentor1', 'mentorpass123')[0] == 401, 'the old password stopped working')
cookie = 'cadhub_session=' + inside("import discord_auth as d; print(d.make_session('mentor1'))")
s, _, b = req('/admin', headers={'Cookie': cookie})
check(s == 200 and 'Seasons' in b and 'Sign out' in b and 'mentor1' in b, 'mentor page with a Discord session')
s, _, b = req('/admin/users', headers={'Cookie': cookie})
check(s == 200 and 'sarah.k' in b and 'Sign out everywhere' in b and 'Password accounts' in b, 'accounts page lists Discord and password accounts')
check(req('/admin/api/heartbeat', data=b'{}', headers={'Cookie': cookie, 'X-Joco-Client': 'addin'})[0] == 403, 'a session cookie never works for the add-in APIs')
token = re.search(r'name="token" value="([0-9a-f]+)"', b).group(1)
s, _, _ = req('/admin/users', data=urllib.parse.urlencode({'action': 'sign-out-everywhere', 'username': 'sarah.k', 'token': token}).encode(),
              headers={'Cookie': cookie, 'Content-Type': 'application/x-www-form-urlencoded'}, origin=False)
check(s == 303, 'posting without the page\'s origin is refused (redirected with an error)')
s, h, _ = req('/admin/users', data=urllib.parse.urlencode({'action': 'sign-out-everywhere', 'username': 'sarah.k', 'token': token}).encode(),
              headers={'Cookie': cookie, 'Content-Type': 'application/x-www-form-urlencoded'})
check(s == 303 and 'ok=' in h.get('Location', ''), 'sign out everywhere ' + h.get('Location', ''))
check(req('/catalog.json', 'sarah.k', key)[0] == 401, 'sarah\'s old key stopped working')
key2 = inside(LINK % ('501', 'sarah.k'))
check(key2 != key and req('/catalog.json', 'sarah.k', key2)[0] == 200, 'signing in again gives a working new key')
s, h, _ = req('/admin/signout', data=b'', headers={'Cookie': cookie})
check(s == 303 and 'cadhub_session=;' in h.get('Set-Cookie', ''), 'sign out clears the cookie')
check(req('/admin', headers={'Cookie': 'cadhub_session=forged.value'})[2].count('Sign in with Discord') == 1, 'a forged session is ignored')

# A password account (not Discord) still signs in with Basic, also on the mentor page.
check(req('/admin', 'sarah', 'sarahpass123')[0] == 403 and req('/catalog.json', 'sarah', 'sarahpass123')[0] == 200, 'password account still works')
check(req('/admin', 'sarah', 'wrong')[0] == 401, 'wrong password refused on the mentor page')
print('PASS: %d Discord sign-in checks through Apache' % n)
