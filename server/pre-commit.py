#!/usr/bin/python3
"""Require locks for existing CAD and properties for all committed CAD.

Directory removal/replacement is intentionally mentor-only in this first server
version. SVN itself verifies client lock tokens in addition to this owner check.
"""
import sys
from svn import core, fs, repos

CAD = (b'.sldprt', b'.sldasm', b'.slddrw')


def check(repository, transaction):
    repository_handle = repos.open(repository)
    filesystem = repos.fs(repository_handle)
    txn = fs.open_txn(filesystem, transaction)
    root = fs.txn_root(txn)
    base = fs.revision_root(filesystem, fs.txn_base_revision(txn))
    author = fs.txn_prop(txn, core.SVN_PROP_REVISION_AUTHOR)
    for path, change in fs.paths_changed2(root).items():
        previous_kind = fs.check_path(base, path)
        removed = change.change_kind in (fs.path_change_delete, fs.path_change_replace)
        if previous_kind == core.svn_node_dir and removed:
            raise ValueError('Directory deletion/replacement requires mentor maintenance: ' + path.decode('utf-8'))
        if not path.lower().endswith(CAD):
            continue
        if previous_kind == core.svn_node_file:
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
        print('JOCO ROBOS CAD: ' + str(exc), file=sys.stderr)
        sys.exit(1)
