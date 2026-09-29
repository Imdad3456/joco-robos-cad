#!/usr/bin/python3
import sys
from svn import fs, repos

try:
    repository = repos.open(sys.argv[1])
    lock = fs.get_lock(repos.fs(repository), sys.argv[2])
    if sys.argv[4] == '1' or lock is None or lock.owner != sys.argv[3].encode():
        raise ValueError('Only the owning working copy may unlock this file. Ask a mentor for recovery.')
except Exception as exc:
    print('JOCO ROBOS CAD: ' + str(exc), file=sys.stderr)
    sys.exit(1)
