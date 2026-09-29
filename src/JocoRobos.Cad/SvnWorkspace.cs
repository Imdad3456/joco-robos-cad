using System;
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
                throw new InvalidOperationException("Could not confirm SVN file information.");
            return info;
        }

        private void RequireIdentity(SvnInfoEventArgs info)
        {
            if (info.RepositoryId != Info.Id)
                throw new InvalidOperationException(Info.Label + " on the server does not match the expected repository. Ask a mentor to check the server.");
        }

        private void RequireWorkspace(SvnClient client)
        {
            WorkspacePolicy.RequireInside(Root, Root);
            if (!IsCheckedOut)
                throw new InvalidOperationException("Click Update first to download " + Info.Label + ".");
            var info = GetInfo(client, new SvnPathTarget(Root));
            RequireIdentity(info);
            if (!SameUri(info.Uri, Repository) ||
                !String.Equals(Path.GetFullPath(client.GetWorkingCopyRoot(Root)).TrimEnd('\\'), Root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The local folder belongs to a different SVN working copy:\n" + Root);
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
                    "\nSubmit your changes first. If this file should not change, keep it and ask a mentor; do not delete or revert it.");
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
                        throw new InvalidOperationException("This folder already contains files but is not an SVN workspace.\n" +
                            "Move it to a safe backup location first. It will not be overwritten.\n" + Root);
                    Directory.CreateDirectory(Path.GetDirectoryName(Root));
                    client.CheckOut(Repository, Root, new SvnCheckOutArgs { Depth = SvnDepth.Infinity, IgnoreExternals = true, AllowObstructions = false });
                }
                else
                {
                    RequireWorkspace(client);
                    ReconcilePendingSubmit(client);
                    foreach (var item in Status(client, Root, false, SvnDepth.Infinity)) RequireClean(item);
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
                if (!SameUri(local.Uri, expected)) throw new InvalidOperationException("This file was switched to another repository location.");
                var remote = GetInfo(client, new SvnUriTarget(expected));
                if (WorkspacePolicy.OwnsLock(login.UserName, local.Lock?.Token, remote.Lock?.Token, remote.Lock?.Owner))
                    return login.UserName;
                if (remote.Lock != null)
                    throw new InvalidOperationException("Locked by " + remote.Lock.Owner + ". You can inspect this file, but cannot edit it.");
                RequireClean(status);
                if (local.LastChangeRevision != remote.LastChangeRevision)
                    throw new InvalidOperationException("This file has a newer server revision. Close the CAD documents and click Update before Edit.");
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
                    throw new InvalidOperationException("This working copy does not own the file's lock.");
                client.Unlock(path, new SvnUnlockArgs { BreakLock = false });
                // A failed request keeps the caller in read-only mode until ownership is rechecked.
            }
        }
    }
}
