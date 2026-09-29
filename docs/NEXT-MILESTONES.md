# Continue after the Windows prototype passes

The student workflow remains **Open Robot → Edit → CAD normally → Submit**. SVN owns history, synchronization, lock tokens, and revisions. The add-in translates those operations into SOLIDWORKS actions. This document records proposed behavior; none of it is implemented in the local prototype.

## Milestones 5–8: SVN and exclusive editing

Start against a disposable repository. Select and pin a SharpSvn release only after checking its .NET Framework/x64 support and native runtime dependencies on the target SOLIDWORKS PC. Keep SVN operations behind a small service boundary; keep SOLIDWORKS COM calls on its UI thread. Serialize workspace operations and prevent double-clicks from starting overlapping work. Move blocking network work off the SOLIDWORKS UI thread.

Use one complete SVN working copy per robot, outside OneDrive or another synchronization tool. Keep SVN metadata intact. Do not recursively overwrite files or implement a second synchronization algorithm. Validate the working copy's repository identity before updates and submissions.

**Open Robot / Update:** checkout only into an absent or empty target directory; do not overwrite an existing CAD folder. On later use, inspect local status and SOLIDWORKS unsaved documents first. Initially require workspace CAD documents to be closed before updating, with a clear save/close instruction if necessary. Never silently reload a dirty document. Preserve local edits and stop on conflicts; do not attempt binary merges. Opening cached CAD offline should be explicitly marked offline and read-only.

**Edit:** resolve a selected component to its actual file where possible, otherwise use the active document. Confirm it is inside this working copy. Virtual components and external references require an explicit supported handling path. Check server revision, refresh the clean file as needed, then request the SVN lock. If HEAD changes during the sequence, stop and refresh before retrying. Verify the local lock token and server ownership before granting writable access, including after network failures. A lock on an assembly does not lock its referenced parts: editing ShooterPlate requires its own lock even when Shooter.SLDASM is locked. Keep the first UI focused on one file, with clear guidance for dependent parts.

After locking, a document opened read-only in SOLIDWORKS may need a supported mode transition or safe reload; changing the filesystem bit alone is insufficient. Prove this with the target SOLIDWORKS version and assembly references before enabling real work. Refuse any reload that would discard unsaved edits.

**Enforcement:** apply `svn:needs-lock` to `.SLDPRT`, `.SLDASM`, and `.SLDDRW`, matching extensions case-insensitively. Also set binary MIME types to discourage merging. SVN's read-only attribute is an accident-prevention aid, not a server authorization boundary. The property alone does not require a lock on commit: see the [Subversion locking documentation](https://svnbook.red-bean.com/en/1.7/svn-book.html). Before production use, add and test server hooks that reject changes to existing CAD files without an appropriate lock owned by the committing user, and require the properties on newly added CAD files. Account for deletion, replacement, and directory operations that affect CAD descendants. SVN's own checks must still validate submitted lock tokens. No student lock stealing; mentor recovery needs a documented procedure.

Gate: two independent Windows working copies and two unique users. Sarah locks a part; Imdad is refused; Imdad can still inspect it. Direct attempts to commit unlocked existing CAD are rejected. Release/commit restores read-only behavior. A stale lock token, offline request, or failed update must never be reported as successful ownership.

## Milestones 9–10: detect and submit

Scan SVN status for modified and unversioned files, excluding temporary lock files, backups, autosaves, SVN metadata, and unrelated files. Do not silently delete missing files. Renames, moves, replacements, and missing dependencies need deliberate handling because assembly references can break.

Show the selected modified/new files and a comment. Check unsaved SOLIDWORKS documents before scanning; obtain normal saves without forcing changes to unlocked files. Validate referenced new parts and include needed parent directories. New CAD files get `svn:needs-lock` and binary MIME properties before their first commit; they need no pre-existing lock. Recheck status and ownership at submission time, then commit the selected coherent set atomically.

Successful SVN commits normally release committed locks unless configured to retain them. Do not blindly unlock all workspace files afterward. Unsubmitted or unchanged locked files need explicit disposition. Preserve edits and locks on a failed commit. An interrupted response may conceal a successful commit: reconcile repository history/status before retrying or declaring failure. Report the confirmed revision and reconcile read-only state only after the outcome is known.

Gate: new nested part plus modified parent assembly, lock denied, stale revision, property-only change, missing file, unsaved document, server hook rejection, and connection interruption. Each must preserve work and report the true outcome. No automatic binary conflict resolution.

## Milestone 11: Steam Deck and cad.imdad.stream

Proposed deployment: persistent SVN repository storage → Apache `mod_dav_svn` with per-user authentication and authorization → Cloudflare Tunnel → HTTPS clients. No separate custom file-upload API is needed. Do not deploy until the host's OS, persistent storage location, and administration access are known.

Test real checkout, update, lock, unlock, and large commits through the tunnel. Confirm WebDAV/SVN methods, request limits, timeouts, and authentication end to end; a successful browser page is insufficient. A browser login challenge must not block the SVN client's requests. Do not disable TLS certificate verification to make the client connect. Keep the origin restricted, give every student a distinct identity, and store credentials through Windows Credential Manager before using real accounts. Do not log secrets or enable plaintext SVN credential caching.

Arrange repository-consistent backups to separate storage and prove restoration, including history and access configuration. Decide how the Deck stays awake and services restart after reboot. A tunnel does not make sleeping hardware available.

## Milestone 12 and later

Build the single Windows installer only after the two-student workflow passes. Bundle the tested SharpSvn/native dependencies, register the x64 COM add-in, configure startup for the intended Windows user, set up the workspace, and provide clean uninstall without deleting CAD. Verify dependency redistribution terms. Authenticode signing and strong-naming are different; signing the installer/add-in is a later distribution task.

Then add status, a Task Pane if useful, safe update notifications, mentor lock recovery, and an optional dashboard. Keep the master assembly under the same exclusive-lock rule as every other CAD file. Students should only need its lock when actually changing it.
