using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using SharpSvn;
using SharpSvn.Security;

namespace JocoRobos.Cad
{
    internal sealed partial class SvnWorkspace
    {
        private readonly NetworkCredential login;
        internal readonly WorkspaceInfo Info;
        private string Root { get { return Info.Root; } }
        private Uri Repository { get { return Info.Repository; } }

        internal SvnWorkspace(NetworkCredential login, WorkspaceInfo info)
        {
            this.login = login;
            Info = info;
        }

        internal bool IsCheckedOut { get { return Directory.Exists(Path.Combine(Root, ".svn")); } }

        private SvnClient Client()
        {
            var client = new SvnClient();
            // No SVN disk password cache, console prompts, or accepting invalid certificates.
            client.Authentication.Clear();
            client.Authentication.SslAuthorityTrustHandlers += SvnAuthentication.SubversionWindowsSslAuthorityTrustHandler;
            var credentials = new CredentialCache();
            credentials.Add(new Uri(Repository.GetLeftPart(UriPartial.Authority) + "/"), "SVN", login);
            client.Authentication.DefaultCredentials = credentials;
            return client;
        }

        internal static T Exclusive<T>(Func<T> action)
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JocoRobos.Cad");
            Directory.CreateDirectory(folder);
            // Cross-process guard for multiple SOLIDWORKS instances of this Windows user.
            using (new FileStream(Path.Combine(folder, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                return action();
        }

        internal void TestConnection()
        {
            using (var client = Client())
                RequireIdentity(GetInfo(client, new SvnUriTarget(Repository)));
        }

        private static SvnInfoEventArgs GetInfo(SvnClient client, SvnTarget target)
        {
            SvnInfoEventArgs info;
            if (!client.GetInfo(target, out info) || info == null)
                throw new InvalidOperationException("Couldn't read the team's information about this file. Check the connection and try again.");
            return info;
        }

        private void RequireIdentity(SvnInfoEventArgs info)
        {
            if (info.RepositoryId != Info.Id)
                throw new InvalidOperationException(Info.Label + " on the server isn't the one this computer expects. Ask a mentor to check the server.");
        }

        private void RequireWorkspace(SvnClient client)
        {
            WorkspacePolicy.RequireInside(Root, Root);
            if (!IsCheckedOut)
                throw new InvalidOperationException("Click Open Robot first to download " + Info.Label + ".");
            var info = GetInfo(client, new SvnPathTarget(Root));
            RequireIdentity(info);
            if (!SameUri(info.Uri, Repository) ||
                !String.Equals(Path.GetFullPath(client.GetWorkingCopyRoot(Root)).TrimEnd('\\'), Root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("This folder is linked to a different team robot, so JOCO won't touch it. Ask a mentor:\n" + Root);
        }

        private static bool SameUri(Uri a, Uri b)
        {
            return a != null && b != null && String.Equals(a.AbsoluteUri.TrimEnd('/'), b.AbsoluteUri.TrimEnd('/'), StringComparison.Ordinal);
        }

        private Uri UrlFor(string path)
        {
            string relative = path.Substring(Root.Length + 1).Replace('\\', '/');
            return new Uri(Repository, String.Join("/", relative.Split('/').Select(Uri.EscapeDataString)));
        }

        private static Collection<SvnStatusEventArgs> Status(SvnClient client, string path, bool remote, SvnDepth depth)
        {
            Collection<SvnStatusEventArgs> result;
            client.GetStatus(path, new SvnStatusArgs { Depth = depth, RetrieveAllEntries = true,
                RetrieveRemoteStatus = remote, RetrieveIgnoredEntries = true, IgnoreExternals = true }, out result);
            if (result == null) throw new InvalidOperationException("Could not inspect the workspace.");
            return result;
        }

        private static void RequireClean(SvnStatusEventArgs item)
        {
            if (!item.Versioned || item.Conflicted || item.Switched || item.IsFileExternal || item.Wedged ||
                item.LocalNodeStatus != SvnStatus.Normal ||
                (item.LocalPropertyStatus != SvnStatus.None && item.LocalPropertyStatus != SvnStatus.Normal))
                throw new InvalidOperationException("Update stopped to preserve local work or an unsupported workspace item:\n" + item.FullPath +
                    "\nSubmit your changes first, or use Tools → JOCO ROBOS CAD → Set Aside My Changes to keep your version as a copy and restore the team's.");
        }

        private void ReconcileReadOnly(SvnClient client)
        {
            // Complete remote status first: a network failure must not grant write access.
            var entries = Status(client, Root, true, SvnDepth.Infinity);
            foreach (var item in entries.Where(x => x.Versioned && WorkspacePolicy.IsCad(x.FullPath) && File.Exists(x.FullPath)))
            {
                WorkspacePolicy.RequireInside(Root, item.FullPath);
                bool owned = !Info.Archived && WorkspacePolicy.OwnsLock(login.UserName, item.LocalLock?.Token, item.RemoteLock?.Token, item.RemoteLock?.Owner);
                FileAttributes attributes = File.GetAttributes(item.FullPath);
                File.SetAttributes(item.FullPath, owned ? attributes & ~FileAttributes.ReadOnly : attributes | FileAttributes.ReadOnly);
            }
        }

        internal long Update()
        {
            using (var client = Client())
            {
                RequireIdentity(GetInfo(client, new SvnUriTarget(Repository)));
                WorkspacePolicy.RequireInside(Root, Root);
                if (!IsCheckedOut)
                {
                    if (File.Exists(Root) || (Directory.Exists(Root) && Directory.EnumerateFileSystemEntries(Root).Any()))
                        throw new InvalidOperationException("This folder already has files that JOCO didn't download.\n" +
                            "Move it to a safe backup location first. It will not be overwritten.\n" + Root);
                    Directory.CreateDirectory(Path.GetDirectoryName(Root));
                    client.CheckOut(Repository, Root, new SvnCheckOutArgs { Depth = SvnDepth.Infinity, IgnoreExternals = true, AllowObstructions = false });
                }
                else
                {
                    RequireWorkspace(client);
                    ReconcilePendingSubmit(client);
                    foreach (var item in Status(client, Root, false, SvnDepth.Infinity))
                        if (!WorkspacePolicy.IsOwnerFile(item.FullPath) || item.Versioned) RequireClean(item);
                    client.Update(Root, new SvnUpdateArgs { Depth = SvnDepth.Infinity, IgnoreExternals = true, AllowObstructions = false });
                }
                RequireWorkspace(client);
                ReconcileReadOnly(client);
                return GetInfo(client, new SvnPathTarget(Root)).Revision;
            }
        }

        internal string Edit(string path)
        {
            path = WorkspacePolicy.RequireInside(Root, path);
            if (!WorkspacePolicy.IsCad(path)) throw new InvalidOperationException("Open a saved SOLIDWORKS part, assembly, or drawing first.");
            if (Info.Archived) throw new InvalidOperationException(Info.Name + " is archived and read-only. Insert parts from the Library, or ask a mentor.");
            using (var client = Client())
            {
                RequireWorkspace(client);
                var status = Status(client, path, false, SvnDepth.Empty).SingleOrDefault();
                if (status == null || !status.Versioned || status.Switched || status.IsFileExternal || status.Conflicted || status.Wedged)
                    throw new InvalidOperationException("This CAD file is not a normal version-controlled file in this workspace.\nNew files do not need Edit; just Submit them.");
                var local = GetInfo(client, new SvnPathTarget(path));
                RequireIdentity(local);
                Uri expected = UrlFor(path);
                if (!SameUri(local.Uri, expected)) throw new InvalidOperationException("This file is linked somewhere unexpected. Ask a mentor.");
                var remote = GetInfo(client, new SvnUriTarget(expected));
                if (WorkspacePolicy.OwnsLock(login.UserName, local.Lock?.Token, remote.Lock?.Token, remote.Lock?.Owner))
                    return login.UserName;
                if (remote.Lock != null && remote.Lock.Owner == login.UserName)
                    throw new InvalidOperationException("You already locked this file from another computer (or before reinstalling).\n\n" +
                        "Submit or Release Edit it there. If that computer isn't available, ask a mentor to release the lock on the Locks page.");
                if (remote.Lock != null)
                    throw new InvalidOperationException("Locked by " + remote.Lock.Owner + ". You can inspect this file, but cannot edit it.");
                RequireClean(status);
                if (local.LastChangeRevision != remote.LastChangeRevision)
                    throw new InvalidOperationException("A teammate submitted a newer version of this file. Close your robot documents to get it (or use Close & Update in the panel), then Edit.");
                string needsLock;
                client.GetProperty(new SvnPathTarget(path), "svn:needs-lock", out needsLock);
                if (needsLock == null) throw new InvalidOperationException("This file is missing its lock policy. Ask a mentor to repair it.");
                client.Lock(path, new SvnLockArgs { StealLock = false, Comment = "Editing in JOCO ROBOS CAD" });
                // Never infer success from the lock call alone; confirm server owner and local token.
                local = GetInfo(client, new SvnPathTarget(path));
                remote = GetInfo(client, new SvnUriTarget(expected));
                if (!WorkspacePolicy.OwnsLock(login.UserName, local.Lock?.Token, remote.Lock?.Token, remote.Lock?.Owner))
                    throw new InvalidOperationException("Lock ownership could not be confirmed. Keep the file read-only and retry Edit when connected.");
                if (local.LastChangeRevision != remote.LastChangeRevision)
                    throw new InvalidOperationException("The server changed during locking. Your lock is retained; close documents and Update before Edit.");
                return login.UserName;
            }
        }

        /// <summary>Recent submits that changed this file, newest first (server read only).</summary>
        internal List<FileVersion> History(string path, int limit)
        {
            path = WorkspacePolicy.RequireInside(Root, path);
            var versions = new List<FileVersion>();
            using (var client = Client())
                client.Log(UrlFor(path), new SvnLogArgs { Limit = limit }, (s, e) => versions.Add(new FileVersion
                {
                    Revision = e.Revision, Author = e.Author ?? "", Time = e.Time.ToLocalTime(), Comment = (e.LogMessage ?? "").Trim(),
                }));
            return versions;
        }

        /// <summary>Saves an older version of a team file as a separate copy (never into the live robot).</summary>
        internal void SaveVersion(string path, long revision, string target)
        {
            path = WorkspacePolicy.RequireInside(Root, path);
            using (var client = Client())
            using (var output = File.Create(target))
                client.Write(new SvnUriTarget(UrlFor(path), revision), output);
            File.SetAttributes(target, File.GetAttributes(target) & ~FileAttributes.ReadOnly);
        }

        /// <summary>True for a file the team doesn't have yet (not versioned, or only scheduled to be added). Local only.</summary>
        internal bool IsNewFile(string path)
        {
            path = WorkspacePolicy.RequireInside(Root, path);
            if (!IsCheckedOut || !File.Exists(path)) return false;
            using (var client = Client())
            {
                Collection<SvnStatusEventArgs> result;
                if (!client.GetStatus(path, new SvnStatusArgs { Depth = SvnDepth.Empty, RetrieveAllEntries = true, ThrowOnError = false }, out result) || result == null) return false;
                var status = result.FirstOrDefault();
                return status == null || status.LocalNodeStatus == SvnStatus.NotVersioned || status.LocalNodeStatus == SvnStatus.Added;
            }
        }

        /// <summary>Unsubmitted work in this folder, checked locally (no network): changed, new, missing files and held locks.</summary>
        internal List<string> LocalWork()
        {
            var work = new List<string>();
            if (!IsCheckedOut) return work;
            using (var client = Client())
                foreach (var item in Status(client, Root, false, SvnDepth.Infinity))
                {
                    string path = Path.GetFullPath(item.FullPath);
                    if (path.TrimEnd('\\').Equals(Root, StringComparison.OrdinalIgnoreCase)) continue;
                    string name = path.Substring(Root.Length + 1);
                    switch (item.LocalNodeStatus)
                    {
                        case SvnStatus.Modified: case SvnStatus.Added: case SvnStatus.Replaced: work.Add(name + " (changed)"); break;
                        case SvnStatus.Missing: case SvnStatus.Deleted: work.Add(name + " (missing)"); break;
                        case SvnStatus.NotVersioned:
                            if (Directory.Exists(path) ? Directory.EnumerateFiles(path, "*.sld*", SearchOption.AllDirectories).Any(WorkspacePolicy.IsSubmittableCad)
                                                       : WorkspacePolicy.IsSubmittableCad(path))
                                work.Add(name + " (new)");
                            break;
                        default:
                            if (item.LocalLock != null) work.Add(name + " (locked by you)");
                            break;
                    }
                }
            return work;
        }

        // Read-only view for the status pane: never changes files, never takes the operation lock.
        internal WorkspaceSnapshot Snapshot()
        {
            if (!IsCheckedOut) return new WorkspaceSnapshot { Info = Info };
            using (var client = Client())
            {
                var local = GetInfo(client, new SvnPathTarget(Root));
                RequireIdentity(local);
                var snapshot = new WorkspaceSnapshot { Info = Info, Local = local.Revision,
                    Head = GetInfo(client, new SvnUriTarget(Repository)).Revision };
                // What this computer already has, file by file: a submit made here updates only its own files' revisions.
                var here = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
                var statuses = Status(client, Root, true, SvnDepth.Infinity);
                foreach (var item in statuses)
                    if (item.Versioned) here[Path.GetFullPath(item.FullPath).TrimEnd('\\')] = item.Revision;
                if (snapshot.Head > snapshot.Local)
                    client.Log(Repository, new SvnLogArgs { Range = new SvnRevisionRange(snapshot.Local + 1, snapshot.Head), RetrieveChangedPaths = true }, (s, e) =>
                    {
                        // Incoming unless every file it changed is already at that revision here, whoever submitted it:
                        // your own submit from another computer still has to come down to this one.
                        if (e.ChangedPaths == null || e.ChangedPaths.Count == 0 || e.ChangedPaths.All(c => AlreadyHere(c, e.Revision, here))) return;
                        string who = e.Author == login.UserName ? "you (another computer)" : e.Author;
                        snapshot.Incoming.Add("#" + e.Revision + " " + who + ": " + (e.LogMessage ?? "").Trim().Split('\n')[0]);
                    });
                snapshot.PendingSubmit = File.Exists(JournalPath);
                foreach (var item in statuses)
                {
                    string path = Path.GetFullPath(item.FullPath);
                    if (item.RemoteLock != null)
                    {
                        snapshot.Locks[path] = item.RemoteLock.Owner;
                        snapshot.LockedSince[path] = item.RemoteLock.CreationTime.ToLocalTime();
                    }
                    if (WorkspacePolicy.OwnsLock(login.UserName, item.LocalLock?.Token, item.RemoteLock?.Token, item.RemoteLock?.Owner))
                        snapshot.Mine.Add(path);
                    if (item.LocalNodeStatus == SvnStatus.Modified) snapshot.Changed.Add(path);
                    else if ((item.LocalNodeStatus == SvnStatus.Added || item.LocalNodeStatus == SvnStatus.NotVersioned) && WorkspacePolicy.IsSubmittableCad(path))
                        snapshot.New.Add(path);
                }
                return snapshot;
            }
        }

        /// <summary>Files whose latest version came from a conversion submit, so Upgrade Robot Files can resume where it stopped.</summary>
        internal HashSet<string> ConvertedFiles()
        {
            var converted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var client = Client())
            {
                RequireWorkspace(client);
                var revisions = new HashSet<long>();
                client.Log(Root, new SvnLogArgs { RetrieveChangedPaths = false }, (s, e) =>
                {
                    if (WorkspacePolicy.IsConversionMessage(e.LogMessage)) revisions.Add(e.Revision);
                });
                if (revisions.Count == 0) return converted;
                foreach (var item in Status(client, Root, false, SvnDepth.Infinity))
                    if (item.Versioned && item.LocalNodeStatus == SvnStatus.Normal && revisions.Contains(item.LastChangeRevision))
                        converted.Add(Path.GetFullPath(item.FullPath));
            }
            return converted;
        }

        /// <summary>
        /// Locks every listed file for a whole-robot operation. Refuses (taking nothing) if any file is changed here,
        /// out of date, or locked by anyone else, and names them.
        /// </summary>
        internal void LockAll(IList<string> paths)
        {
            using (var client = Client())
            {
                RequireWorkspace(client);
                var wanted = new HashSet<string>(paths.Select(p => WorkspacePolicy.RequireInside(Root, p)), StringComparer.OrdinalIgnoreCase);
                var problems = new List<string>();
                var toLock = new List<string>();
                foreach (var item in Status(client, Root, true, SvnDepth.Infinity))
                {
                    string path = Path.GetFullPath(item.FullPath);
                    if (!wanted.Contains(path)) continue;
                    string name = path.Substring(Root.Length + 1);
                    if (item.LocalNodeStatus != SvnStatus.Normal) problems.Add(name + " — changed on this computer (Submit or Set Aside it first)");
                    else if (item.IsRemoteUpdated) problems.Add(name + " — out of date (Update first)");
                    else if (WorkspacePolicy.OwnsLock(login.UserName, item.LocalLock?.Token, item.RemoteLock?.Token, item.RemoteLock?.Owner)) continue;
                    else if (item.RemoteLock != null) problems.Add(name + " — " + item.RemoteLock.Owner + " is editing it");
                    else toLock.Add(path);
                }
                if (problems.Count > 0)
                    throw new InvalidOperationException("Nothing was changed. These files need attention first:\n\n" + String.Join("\n", problems.Take(12)) +
                        (problems.Count > 12 ? "\n…and " + (problems.Count - 12) + " more" : ""));
                if (toLock.Count > 0)
                {
                    try { client.Lock(toLock, new SvnLockArgs { StealLock = false, Comment = "Upgrade Robot Files" }); }
                    catch (SvnException failure)
                    {
                        // Someone started editing in the meantime: give back whatever this took.
                        try { ReleaseUnchangedLocks(); } catch (Exception) { }
                        throw new InvalidOperationException("Could not lock every file; a teammate may have just started editing. Nothing was changed.\n\n" + failure.Message, failure);
                    }
                }
                ReconcileReadOnly(client);
            }
        }

        private bool AlreadyHere(SvnChangeItem change, long revision, IDictionary<string, long> here)
        {
            string local = Path.GetFullPath(Path.Combine(Root, change.Path.TrimStart('/').Replace('/', '\\'))).TrimEnd('\\');
            if (change.Action == SvnChangeAction.Delete) return !File.Exists(local) && !Directory.Exists(local);
            long have;
            return here.TryGetValue(local, out have) && have >= revision;
        }

        /// <summary>Releases every lock this computer holds on a file that is unchanged; changed files keep theirs.</summary>
        internal List<string> ReleaseUnchangedLocks()
        {
            var released = new List<string>();
            if (!IsCheckedOut) return released;
            using (var client = Client())
            {
                RequireWorkspace(client);
                foreach (var item in Status(client, Root, true, SvnDepth.Infinity))
                {
                    string path = Path.GetFullPath(item.FullPath);
                    if (item.NodeKind != SvnNodeKind.File || item.LocalNodeStatus != SvnStatus.Normal) continue;
                    if (item.LocalPropertyStatus != SvnStatus.None && item.LocalPropertyStatus != SvnStatus.Normal) continue;
                    if (!WorkspacePolicy.OwnsLock(login.UserName, item.LocalLock?.Token, item.RemoteLock?.Token, item.RemoteLock?.Owner)) continue;
                    released.Add(WorkspacePolicy.RequireInside(Root, path));
                }
                if (released.Count > 0)
                {
                    client.Unlock(released, new SvnUnlockArgs { BreakLock = false });
                    ReconcileReadOnly(client);
                }
            }
            return released;
        }

        internal void ReleaseEdit(string path)
        {
            path = WorkspacePolicy.RequireInside(Root, path);
            if (!WorkspacePolicy.IsCad(path)) throw new InvalidOperationException("Select a CAD file first.");
            using (var client = Client())
            {
                RequireWorkspace(client);
                var status = Status(client, path, true, SvnDepth.Empty).SingleOrDefault();
                if (status == null) throw new InvalidOperationException("Could not inspect the file.");
                RequireClean(status);
                if (!WorkspacePolicy.OwnsLock(login.UserName, status.LocalLock?.Token, status.RemoteLock?.Token, status.RemoteLock?.Owner))
                    throw new InvalidOperationException("This computer isn't the one editing this file.");
                client.Unlock(path, new SvnUnlockArgs { BreakLock = false });
                // A failed request keeps the caller in read-only mode until ownership is rechecked.
            }
        }
    }

}
