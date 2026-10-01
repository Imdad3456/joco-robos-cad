#!/usr/bin/python3
"""Require locks for existing CAD and properties for all committed CAD.

Directory removal/replacement is mentor-only. SVN itself verifies client lock
tokens in addition to this owner check. The one exception is a mentor's "Undo
this submit" from the admin page: it carries the joco:mentor-undo revision
property, and SVN still refuses it for any file a student currently has locked.
"""
import json
import sys
from svn import core, fs, repos

STATE = '/etc/joco/state.json'


def is_mentor(author):
    try:
        with open(STATE) as handle:
            return author is not None and author.decode('utf-8') in json.load(handle).get('mentors', [])
    except (OSError, ValueError):
        return False

CAD = (b'.sldprt', b'.sldasm', b'.slddrw')


def check(repository, transaction):
    repository_handle = repos.open(repository)
    filesystem = repos.fs(repository_handle)
    txn = fs.open_txn(filesystem, transaction)
    root = fs.txn_root(txn)
    base = fs.revision_root(filesystem, fs.txn_base_revision(txn))
    author = fs.txn_prop(txn, core.SVN_PROP_REVISION_AUTHOR)
    mentor_undo = fs.txn_prop(txn, b'joco:mentor-undo') is not None and is_mentor(author)
    for path, change in fs.paths_changed2(root).items():
        previous_kind = fs.check_path(base, path)
        removed = change.change_kind in (fs.path_change_delete, fs.path_change_replace)
        if previous_kind == core.svn_node_dir and removed and not mentor_undo:
            raise ValueError('Directory deletion/replacement requires mentor maintenance: ' + path.decode('utf-8'))
        if not path.lower().endswith(CAD):
            continue
        if previous_kind == core.svn_node_file and not mentor_undo:
            lock = fs.get_lock(filesystem, path)
            if lock is None or lock.owner != author:
                raise ValueError('Click Edit and acquire your own lock before submitting: ' + path.decode('utf-8'))
        if fs.check_path(root, path) == core.svn_node_file:
            if fs.node_prop(root, path, b'svn:needs-lock') is None:
                raise ValueError('CAD requires svn:needs-lock: ' + path.decode('utf-8'))
            if fs.node_prop(root, path, b'svn:mime-type') != b'application/octet-stream':
                raise ValueError('CAD requires binary MIME type: ' + path.decode('utf-8'))


if __name__ == '__main__':
    try:
        check(sys.argv[1], sys.argv[2])
    except Exception as exc:
        print('CAD Hub: ' + str(exc), file=sys.stderr)
        sys.exit(1)
