using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using SharpSvn;

namespace JocoRobos.Cad
{
    internal enum ImportPathState { Free, Committed, Unknown }

    internal sealed class SubmitResult
    {
        internal long Revision;
        internal readonly List<string> Warnings = new List<string>();
    }

    internal sealed partial class SvnWorkspace
    {
        private string JournalPath
        {
            get { return System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JocoRobos.Cad", "pending-submit-" + Info.Name + ".txt"); }
        }

        private SubmitItem Item(SubmitKind kind, string path)
        {
            return new SubmitItem { Kind = kind, Path = path, Workspace = Info };
        }

        private SubmitItem Held(string path, string reason)
        {
            return new SubmitItem { Kind = SubmitKind.Modified, Path = path, Workspace = Info, Reason = reason };
        }

        internal SubmitPlan PrepareSubmit()
        {
            using (var client = Client())
            {
                RequireWorkspace(client);
                var plan = new SubmitPlan { Notice = ReconcilePendingSubmit(client) };
                Scan(client, plan);
                return plan;
            }
        }

        private void Scan(SvnClient client, SubmitPlan plan)
        {
            // Remote status is required: lock ownership is decided by the server, not the local token alone.
            foreach (var item in Status(client, Root, true, SvnDepth.Infinity))
            {
                string path = System.IO.Path.GetFullPath(item.FullPath).TrimEnd('\\');
                if (path.Equals(Root, StringComparison.OrdinalIgnoreCase)) continue;
                WorkspacePolicy.RequireInside(Root, path);
                string relative = (Info.IsLibrary ? "Library\\" : "") + path.Substring(Root.Length + 1);
                SvnStatus local = item.LocalNodeStatus;
                bool owned = WorkspacePolicy.OwnsLock(login.UserName, item.LocalLock?.Token, item.RemoteLock?.Token, item.RemoteLock?.Owner);

                if (local == SvnStatus.NotVersioned || local == SvnStatus.Ignored)
                {
                    if (Directory.Exists(path)) AddNewFiles(path, plan);
                    else if (WorkspacePolicy.IsSubmittableCad(path)) plan.Items.Add(Item(SubmitKind.New, path));
                    continue;
                }
                if (item.Conflicted || item.Wedged || item.Switched || item.IsFileExternal)
                {
                    plan.Blocked.Add(relative + " — needs mentor repair");
                    continue;
                }
                if (item.NodeKind == SvnNodeKind.Directory)
                {
                    if (local != SvnStatus.Normal && local != SvnStatus.Added)
                        plan.Blocked.Add(relative + " — folder is " + local.ToString().ToLowerInvariant() + "; ask a mentor");
                    continue;
                }
                switch (local)
                {
                    case SvnStatus.None:
                        break; // Only on the server; Update downloads it.
                    case SvnStatus.Normal:
                        bool propsChanged = item.LocalPropertyStatus == SvnStatus.Modified;
                        if (propsChanged) plan.Blocked.Add(relative + " — its team settings were changed on this computer; ask a mentor");
                        else if (owned) plan.Items.Add(Item(SubmitKind.ReleaseOnly, path));
                        break;
                    case SvnStatus.Added:
                        // Left behind by a Submit that did not reach the server.
                        if (WorkspacePolicy.IsSubmittableCad(path)) plan.Items.Add(Item(SubmitKind.New, path));
                        else plan.Blocked.Add(relative + " — not a SOLIDWORKS file");
                        break;
                    case SvnStatus.Modified:
                        if (item.LocalPropertyStatus == SvnStatus.Modified)
                            plan.Blocked.Add(relative + " — its team settings were changed on this computer; ask a mentor");
                        else if (!WorkspacePolicy.IsCad(path) || owned)
                            plan.Items.Add(Item(SubmitKind.Modified, path));
                        else if (item.RemoteLock != null && item.RemoteLock.Owner == login.UserName)
                            plan.SetAside.Add(Held(path, "Changed, but you locked it from another computer. Submit it there, or ask a mentor to release that lock."));
                        else if (item.RemoteLock != null)
                            plan.SetAside.Add(Held(path, "Changed, but " + item.RemoteLock.Owner + " is editing it. Use Tools → CAD Hub → Set Aside My Changes to keep your version separately."));
                        else if (item.IsRemoteUpdated)
                            plan.SetAside.Add(Held(path, "Changed, but a teammate submitted a newer version. Use Tools → CAD Hub → Set Aside My Changes, then Update."));
                        else
                        {
                            var free = Item(SubmitKind.Modified, path);
                            free.NeedsLock = true;
                            plan.Items.Add(free);
                        }
                        break;
                    case SvnStatus.Missing:
                    case SvnStatus.Deleted:
                        plan.Restore.Add(Held(path, "Missing. If it was deleted by accident, Restore brings it back. Renaming or removing team CAD is a mentor task."));
                        break;
                    default:
                        plan.Blocked.Add(relative + " — " + local.ToString().ToLowerInvariant() + "; ask a mentor");
                        break;
                }
            }
        }

        private void AddNewFiles(string directory, SubmitPlan plan)
        {
            // SVN reports an unversioned folder as one entry; look inside for new CAD without following links.
            WorkspacePolicy.RequireInside(Root, directory);
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) continue;
                if (Directory.Exists(entry)) AddNewFiles(entry, plan);
                else if (WorkspacePolicy.IsSubmittableCad(entry)) plan.Items.Add(Item(SubmitKind.New, entry));
            }
        }

        internal SubmitResult Submit(IList<SubmitItem> selected, string comment)
        {
            comment = WorkspacePolicy.RequireComment(comment);
            selected = selected.Where(x => x.Workspace.Name == Info.Name).ToList();
            if (selected.Count == 0) throw new InvalidOperationException("Select at least one file.");
            using (var client = Client())
            {
                RequireWorkspace(client);
                // Recheck everything at submission time; the review dialog may have been open for a while.
                var current = new SubmitPlan();
                if (ReconcilePendingSubmit(client) != null)
                    throw new InvalidOperationException("An earlier Submit was just confirmed on the server. Review your changes and Submit again.");
                Scan(client, current);
                foreach (var item in selected)
                    if (!current.Items.Any(x => x.Kind == item.Kind && x.Path.Equals(item.Path, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidOperationException("Your files changed while reviewing:\n" + item.Relative + "\nNothing was submitted. Click Submit again.");
                // Files changed without Edit: take the lock now. SVN refuses if anyone holds it or the file is out of date.
                var unlocked = current.Items.Where(x => x.NeedsLock && selected.Any(y => y.Path.Equals(x.Path, StringComparison.OrdinalIgnoreCase))).Select(x => x.Path).ToList();
                if (unlocked.Count > 0)
                {
                    try { client.Lock(unlocked, new SvnLockArgs { StealLock = false, Comment = "Locked by Submit" }); }
                    catch (SvnException failure)
                    {
                        throw new InvalidOperationException("Could not lock your changed files; a teammate may have just started editing them. Nothing was submitted.\n\n" + failure.Message, failure);
                    }
                    foreach (var status in Status(client, Root, true, SvnDepth.Infinity).Where(x => unlocked.Contains(Path.GetFullPath(x.FullPath), StringComparer.OrdinalIgnoreCase)))
                        if (!WorkspacePolicy.OwnsLock(login.UserName, status.LocalLock?.Token, status.RemoteLock?.Token, status.RemoteLock?.Owner))
                            throw new InvalidOperationException("Could not confirm your lock on " + Path.GetFileName(status.FullPath) + ". Nothing was submitted; try again.");
                }

                var commit = selected.Where(x => x.Kind != SubmitKind.ReleaseOnly).ToList();
                var release = selected.Where(x => x.Kind == SubmitKind.ReleaseOnly).Select(x => x.Path).ToList();
                var result = new SubmitResult();
                if (commit.Count > 0)
                {
                    var targets = new List<string>();
                    foreach (var item in commit.Where(x => x.Kind == SubmitKind.New))
                        targets.AddRange(ScheduleAdd(client, item.Path));
                    targets.AddRange(commit.Select(x => x.Path));
                    targets = targets.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                    File.WriteAllLines(JournalPath, targets.Select(x => x.Substring(Root.Length + 1)));
                    SvnCommitResult committed;
                    try
                    {
                        // Explicit targets with empty depth: only these files' lock tokens are sent and released.
                        client.Commit(targets, new SvnCommitArgs { LogMessage = comment, Depth = SvnDepth.Empty, KeepLocks = false }, out committed);
                    }
                    catch (Exception failure)
                    {
                        string outcome;
                        try { outcome = ReconcilePendingSubmit(client); }
                        catch { outcome = null; }
                        if (outcome == null)
                            throw new InvalidOperationException("Submit did not complete. Your edits and locks are kept; try Submit again when connected.\n\n" + failure.Message, failure);
                        result.Warnings.Add(outcome);
                        committed = null;
                    }
                    if (committed != null)
                    {
                        result.Revision = committed.Revision;
                        if (!String.IsNullOrEmpty(committed.PostCommitError)) result.Warnings.Add("Server note: " + committed.PostCommitError);
                        File.Delete(JournalPath);
                    }
                    else if (result.Warnings.Count == 0)
                    {
                        File.Delete(JournalPath);
                        throw new InvalidOperationException("The server reported nothing to submit.");
                    }
                }
                if (release.Count > 0)
                {
                    try { client.Unlock(release, new SvnUnlockArgs { BreakLock = false }); }
                    catch (Exception failure) { result.Warnings.Add("Unchanged files are still locked; use Release Edit later. " + failure.Message); }
                }
                try { ReconcileReadOnly(client); }
                catch (Exception failure) { result.Warnings.Add("Could not refresh read-only files; click Update later. " + failure.Message); }
                return result;
            }
        }

        /// <summary>
        /// For an FRCDesignLib import at a server-assigned Library path: Committed if the Library already has the file
        /// (updating it here if needed), otherwise clears any leftover from an earlier failed attempt and returns Free.
        /// </summary>
        internal ImportPathState PrepareImportPath(string path)
        {
            path = WorkspacePolicy.RequireInside(Root, path);
            using (var client = Client())
            {
                RequireWorkspace(client);
                var args = new SvnInfoArgs();
                args.AddExpectedError(SvnErrorCode.SVN_ERR_RA_ILLEGAL_URL, SvnErrorCode.SVN_ERR_FS_NOT_FOUND);
                System.Collections.ObjectModel.Collection<SvnInfoEventArgs> infos;
                client.GetInfo(new SvnUriTarget(UrlFor(path), SvnRevision.Head), args, out infos);
                bool onServer = infos != null && infos.Count > 0;
                var status = IsVersioned(client, System.IO.Path.GetDirectoryName(path)) ? Status(client, path, false, SvnDepth.Empty).FirstOrDefault() : null;
                if (onServer)
                {
                    if (status == null || status.LocalNodeStatus != SvnStatus.Normal)
                    {
                        // Our own leftover (never someone's work): replace it with the committed copy.
                        if (status != null && status.LocalNodeStatus == SvnStatus.Added) client.Revert(path, new SvnRevertArgs { Depth = SvnDepth.Empty });
                        if (File.Exists(path)) File.Delete(path);
                        client.Update(Root, new SvnUpdateArgs { Depth = SvnDepth.Infinity, IgnoreExternals = true, AllowObstructions = false });
                        ReconcileReadOnly(client);
                    }
                    return ImportPathState.Committed;
                }
                if (status != null && status.LocalNodeStatus == SvnStatus.Added) client.Revert(path, new SvnRevertArgs { Depth = SvnDepth.Empty });
                if (File.Exists(path)) File.Delete(path);
                RemoveEmptyAddedFolders(client, System.IO.Path.GetDirectoryName(path));
                return ImportPathState.Free;
            }
        }

        private void RemoveEmptyAddedFolders(SvnClient client, string folder)
        {
            while (folder != null && folder.Length > Root.Length && Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                var status = IsVersioned(client, folder) ? Status(client, folder, false, SvnDepth.Empty).FirstOrDefault() : null;
                if (status != null && status.LocalNodeStatus != SvnStatus.Added) break; // A real Library folder: keep it.
                if (status != null) client.Revert(folder, new SvnRevertArgs { Depth = SvnDepth.Empty });
                Directory.Delete(folder);
                folder = System.IO.Path.GetDirectoryName(folder);
            }
        }

        /// <summary>Brings back the team's copy of files deleted or missing on this computer. Never touches other files.</summary>
        internal int RestoreDeleted(IList<SubmitItem> items)
        {
            using (var client = Client())
            {
                RequireWorkspace(client);
                int restored = 0;
                foreach (var item in items.Where(x => x.Workspace.Name == Info.Name))
                {
                    string path = WorkspacePolicy.RequireInside(Root, item.Path);
                    var status = Status(client, path, false, SvnDepth.Empty).SingleOrDefault();
                    if (status == null || (status.LocalNodeStatus != SvnStatus.Missing && status.LocalNodeStatus != SvnStatus.Deleted)) continue;
                    client.Revert(path, new SvnRevertArgs { Depth = SvnDepth.Empty });
                    restored++;
                }
                ReconcileReadOnly(client);
                return restored;
            }
        }

        /// <summary>Copies each file to C:\JOCO-ROBOS\Set Aside\&lt;time&gt;\&lt;season&gt;\..., checks the copy, then restores the team's version.</summary>
        internal string SetAside(IList<SubmitItem> items)
        {
            using (var client = Client())
            {
                RequireWorkspace(client);
                string folder = Path.Combine(WorkspacePolicy.UniqueFolder(Path.Combine(WorkspaceInfo.BaseFolder, "Set Aside"), DateTime.Now), Info.Name);
                foreach (var item in items.Where(x => x.Workspace.Name == Info.Name))
                {
                    string path = WorkspacePolicy.RequireInside(Root, item.Path);
                    var status = Status(client, path, false, SvnDepth.Empty).SingleOrDefault();
                    if (status == null || status.LocalNodeStatus != SvnStatus.Modified)
                        throw new InvalidOperationException(item.Relative + " is no longer changed. Nothing was moved.");
                    string copy = Path.Combine(folder, item.Relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(copy));
                    File.Copy(path, copy, false);
                    File.SetAttributes(copy, File.GetAttributes(copy) & ~FileAttributes.ReadOnly);
                    // Never discard the only copy: revert only after the saved copy is proven identical.
                    if (!File.ReadAllBytes(copy).SequenceEqual(File.ReadAllBytes(path)))
                        throw new InvalidOperationException("Could not save a safe copy of " + item.Relative + ". Nothing was changed.");
                    client.Revert(path, new SvnRevertArgs { Depth = SvnDepth.Empty });
                }
                ReconcileReadOnly(client);
                return folder;
            }
        }

        private List<string> ScheduleAdd(SvnClient client, string file)
        {
            // Every new folder on the way goes into the commit: also ones already marked for adding by an earlier Submit that
            // failed. Leaving those out made SVN refuse the file ("Commit failed") on every try after the first.
            var parents = WorkspacePolicy.UnversionedParents(Root, file, dir => IsCommitted(client, dir));
            foreach (string dir in parents)
                if (!IsVersioned(client, dir)) client.Add(dir, new SvnAddArgs { Depth = SvnDepth.Empty });
            if (!IsVersioned(client, file)) client.Add(file, new SvnAddArgs { Depth = SvnDepth.Empty });
            // Required by the server hook and makes the file read-only for everyone who has not clicked Edit.
            client.SetProperty(file, "svn:needs-lock", "*");
            client.SetProperty(file, "svn:mime-type", "application/octet-stream");
            return parents;
        }

        // On the server already: versioned and not just marked for adding.
        private static bool IsCommitted(SvnClient client, string path)
        {
            System.Collections.ObjectModel.Collection<SvnInfoEventArgs> infos;
            return client.GetInfo(new SvnPathTarget(path), new SvnInfoArgs { ThrowOnError = false }, out infos) &&
                infos != null && infos.Count > 0 && infos[0].Schedule != SvnSchedule.Add;
        }

        private static bool IsVersioned(SvnClient client, string path)
        {
            // Unversioned paths (including those under unversioned folders) report an error instead of info.
            System.Collections.ObjectModel.Collection<SvnInfoEventArgs> infos;
            return client.GetInfo(new SvnPathTarget(path), new SvnInfoArgs { ThrowOnError = false }, out infos) &&
                infos != null && infos.Count > 0;
        }

        // A dropped connection can hide a successful commit. Compare the server with the journaled files
        // before retrying: returns a message when the earlier Submit landed, null when nothing was pending
        // or it never reached the server (edits and locks are then untouched).
        private string ReconcilePendingSubmit(SvnClient client)
        {
            if (!File.Exists(JournalPath)) return null;
            client.CleanUp(Root);
            var paths = File.ReadAllLines(JournalPath).Where(x => x.Length > 0)
                .Select(x => WorkspacePolicy.RequireInside(Root, System.IO.Path.Combine(Root, x))).ToList();
            var landed = new List<SvnStatusEventArgs>();
            var pending = new List<string>();
            long revision = 0;
            foreach (string path in paths.Where(File.Exists))
            {
                var status = Status(client, path, false, SvnDepth.Empty).SingleOrDefault();
                if (status == null || (status.LocalNodeStatus != SvnStatus.Modified && status.LocalNodeStatus != SvnStatus.Added)) continue;
                var args = new SvnInfoArgs();
                args.AddExpectedError(SvnErrorCode.SVN_ERR_RA_ILLEGAL_URL, SvnErrorCode.SVN_ERR_FS_NOT_FOUND);
                System.Collections.ObjectModel.Collection<SvnInfoEventArgs> infos;
                client.GetInfo(new SvnUriTarget(UrlFor(path), SvnRevision.Head), args, out infos);
                var remote = infos == null ? null : infos.FirstOrDefault();
                if (remote != null && remote.LastChangeAuthor == login.UserName &&
                    remote.LastChangeRevision > Math.Max(0, status.Revision) && SameContent(client, path, remote.Uri))
                {
                    landed.Add(status);
                    revision = Math.Max(revision, remote.LastChangeRevision);
                }
                else pending.Add(path);
            }
            // A commit is all-or-nothing, so a mix means some files were saved again after the interrupted Submit reached the
            // server. The landed ones match the server exactly (safe to settle); the others are newer work and stay as they are.
            if (landed.Count > 0)
            {
                // The server already has these exact bytes, so reverting loses nothing; Update then downloads them.
                foreach (var status in landed)
                {
                    client.Revert(status.FullPath, new SvnRevertArgs { Depth = SvnDepth.Empty });
                    if (status.LocalNodeStatus == SvnStatus.Added) File.Delete(status.FullPath);
                }
                foreach (string dir in paths.Where(Directory.Exists).OrderByDescending(x => x.Length))
                {
                    var status = Status(client, dir, false, SvnDepth.Empty).SingleOrDefault();
                    if (status != null && status.LocalNodeStatus == SvnStatus.Added && !Directory.EnumerateFileSystemEntries(dir).Any())
                    {
                        client.Revert(dir, new SvnRevertArgs { Depth = SvnDepth.Empty });
                        Directory.Delete(dir);
                    }
                }
            }
            File.Delete(JournalPath);
            if (landed.Count == 0) return null;
            string done = "Your earlier Submit did reach the team (" + landed.Count + (landed.Count == 1 ? " file" : " files") + "), so nothing was lost or sent twice.";
            if (pending.Count == 0) return done + " Your computer finishes catching up at the next update (automatic when your robot documents are closed).";
            return done + " You changed " + String.Join(", ", pending.Take(5).Select(System.IO.Path.GetFileName)) + (pending.Count > 5 ? ", …" : "") +
                " again after that; those newer changes are kept. If Submit can't send them, use Tools → CAD Hub → Set Aside My Changes to keep a copy, then Edit and redo them.";
        }

        private static bool SameContent(SvnClient client, string path, Uri url)
        {
            byte[] local, remote;
            using (var sha = SHA1.Create())
            using (var stream = File.OpenRead(path)) local = sha.ComputeHash(stream);
            using (var sha = SHA1.Create())
            using (var hashing = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write))
            {
                client.Write(new SvnUriTarget(url, SvnRevision.Head), hashing);
                hashing.FlushFinalBlock();
                remote = sha.Hash;
            }
            return local.SequenceEqual(remote);
        }
    }
}
