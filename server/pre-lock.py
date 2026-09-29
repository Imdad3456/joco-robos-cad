#!/usr/bin/python3
import sys
from svn import fs, repos

try:
    repository = repos.open(sys.argv[1])
    if fs.get_lock(repos.fs(repository), sys.argv[2]) is not None:
        raise ValueError('This file is already locked. Ask its owner to submit or unlock it; lock stealing is disabled.')
except Exception as exc:
    print('JOCO ROBOS CAD: ' + str(exc), file=sys.stderr)
    sys.exit(1)
