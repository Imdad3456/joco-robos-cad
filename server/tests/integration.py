#!/usr/bin/python3
"""Run inside the server container. Uses a disposable repo and accounts only."""
import os
from pathlib import Path
import secrets
import shutil
import subprocess
import tempfile
import urllib.request
import urllib.error


def run(*args, ok=True, **kwargs):
    result = subprocess.run(args, text=True, capture_output=True, **kwargs)
    if ok and result.returncode:
        raise AssertionError(result.stderr)
    if ok is False and result.returncode == 0:
        raise AssertionError('Operation unexpectedly succeeded: ' + args[1])
    return result


def main():
    suffix = secrets.token_hex(5)
    name = 'validation-' + suffix
    repository = Path('/var/lib/svn') / name
    base_url = os.environ.get('JOCO_TEST_BASE_URL', 'http://127.0.0.1/svn').rstrip('/')
    url = base_url + '/' + name
    users = Path('/etc/joco/users')
    saved_users = users.read_bytes()
    password = secrets.token_urlsafe(30)
    alice, bob = 'test-a-' + suffix, 'test-b-' + suffix

    def svn(user, *args, ok=True):
        return run('svn', '--non-interactive', '--no-auth-cache', '--username', user,
                   '--password', password, *map(str, args), ok=ok)

    try:
        for user in (alice, bob):
            run('htpasswd', '-iB', str(users), user, input=password + '\n')
        run('svnadmin', 'create', str(repository))
        for hook in ('pre-commit', 'pre-lock', 'pre-unlock'):
            shutil.copy('/opt/joco/' + hook, repository / 'hooks' / hook)
        run('chown', '-R', 'www-data:www-data', str(repository))
        try:
            request = urllib.request.Request(url, headers={'User-Agent': 'SVN/1.14.2 (JOCO integration test)'})
            urllib.request.urlopen(request, timeout=20)
            raise AssertionError('Anonymous repository access succeeded')
        except urllib.error.HTTPError as exc:
            assert exc.code == 401, exc.code
        print('PASS anonymous access denied', flush=True)
        with tempfile.TemporaryDirectory() as temp:
            a, b = Path(temp) / 'a', Path(temp) / 'b'
            svn(alice, 'checkout', url, a)
            (a / 'Subsystem').mkdir()
            part = a / 'Subsystem' / 'Plate.SLDPRT'
            part.write_bytes(b'TEST FIXTURE ONLY - not a SOLIDWORKS document\n')
            svn(alice, 'add', a / 'Subsystem')
            failure = svn(alice, 'commit', a, '-m', 'Reject missing properties', ok=False)
            assert 'svn:needs-lock' in failure.stderr, failure.stderr
            svn(alice, 'propset', 'svn:needs-lock', '*', part)
            failure = svn(alice, 'commit', a, '-m', 'Reject missing binary MIME', ok=False)
            assert 'binary MIME' in failure.stderr, failure.stderr
            svn(alice, 'propset', 'svn:mime-type', 'application/octet-stream', part)
            svn(alice, 'commit', a, '-m', 'Add fixture')
            svn(bob, 'checkout', url, b)
            other = b / 'Subsystem' / part.name
            assert not (other.stat().st_mode & 0o222)
            print('PASS checkout, new CAD properties, and read-only files', flush=True)
            os.chmod(part, 0o644)
            part.write_bytes(b'Unlocked edit\n')
            failure = svn(alice, 'commit', a, '-m', 'Reject unlocked change', ok=False)
            assert 'acquire your own lock' in failure.stderr, failure.stderr
            svn(alice, 'lock', part)
            denied = svn(bob, 'lock', other, ok=None)
            # SVN may return success with a warning for a failed individual lock.
            assert 'locked' in denied.stderr.lower(), denied.stderr
            stolen = svn(bob, 'lock', '--force', other, ok=None)
            assert 'stealing is disabled' in stolen.stderr, stolen.stderr
            broken = svn(bob, 'unlock', '--force', other, ok=None)
            assert 'owning working copy' in broken.stderr, broken.stderr
            svn(alice, 'commit', a, '-m', 'Submit locked fixture')
            assert not (part.stat().st_mode & 0o222)
            svn(bob, 'update', b)
            assert other.read_bytes() == b'Unlocked edit\n'
            svn(bob, 'lock', other)
            assert other.stat().st_mode & 0o200
            svn(bob, 'unlock', other)
            assert not (other.stat().st_mode & 0o222)
            print('PASS exclusive lock, no stealing/breaking, commit release, update, unlock', flush=True)
            svn(bob, 'delete', other)
            failure = svn(bob, 'commit', b, '-m', 'Reject unlocked deletion', ok=False)
            assert 'acquire your own lock' in failure.stderr, failure.stderr
            svn(bob, 'revert', other)
            svn(bob, 'delete', b / 'Subsystem')
            failure = svn(bob, 'commit', b, '-m', 'Reject directory deletion', ok=False)
            assert 'Directory deletion/replacement' in failure.stderr, failure.stderr
            print('PASS unlocked deletion and directory deletion denied', flush=True)
            run('svnadmin', 'verify', str(repository))
            backup = Path(temp) / 'backup'
            run('svnadmin', 'hotcopy', str(repository), str(backup))
            run('svnadmin', 'verify', str(backup))
            svn(alice, 'checkout', 'file://' + str(backup), Path(temp) / 'restored')
            assert (Path(temp) / 'restored/Subsystem/Plate.SLDPRT').read_bytes() == b'Unlocked edit\n'
            print('PASS backup verification and restored checkout', flush=True)
    finally:
        users.write_bytes(saved_users)
        shutil.rmtree(repository, ignore_errors=True)


if __name__ == '__main__':
    main()
