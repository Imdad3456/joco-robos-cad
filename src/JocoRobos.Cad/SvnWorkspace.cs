using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using SharpSvn;
using SharpSvn.Security;

namespace JocoRobos.Cad
{
    internal sealed class SvnWorkspace
    {
        internal const string Root = @"C:\JOCO-ROBOS\2027-Robot";
        internal static readonly string RobotPath = Path.Combine(Root, @"00_Master\Robot.SLDASM");
        internal static readonly Uri Repository = new Uri("https://cad.imdad.stream/svn/2027-Robot/");
        private static readonly Guid RepositoryId = new Guid("b8f359f6-f382-4c21-998e-c00f2827aa38");
        private readonly NetworkCredential login;

        internal SvnWorkspace(NetworkCredential login) { this.login = login; }

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

        internal T Exclusive<T>(Func<T> action)
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
            {
                var info = Info(client, new SvnUriTarget(Repository));
                RequireIdentity(info);
            }
        }

        private static SvnInfoEventArgs Info(SvnClient client, SvnTarget target)
        {
            SvnInfoEventArgs info;
            if (!client.GetInfo(target, out info) || info == null)
                throw new InvalidOperationException("Could not confirm SVN file information.");
            return info;
        }

        private static void RequireIdentity(SvnInfoEventArgs info)
        {
            if (info.RepositoryId != RepositoryId)
                throw new InvalidOperationException("This is not the configured JOCO ROBOS repository. Ask a mentor to check the server.");
        }

        private static void RequireWorkspace(SvnClient client)
        {
            WorkspacePolicy.RequireInside(Root, Root);
            if (!Directory.Exists(Path.Combine(Root, ".svn")))
                throw new InvalidOperationException("Click Update first to create the robot workspace.");
            var info = Info(client, new SvnPathTarget(Root));
            RequireIdentity(info);
            if (!SameUri(info.Uri, Repository) ||
                !String.Equals(Path.GetFullPath(client.GetWorkingCopyRoot(Root)).TrimEnd('\\'), Root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The local folder belongs to a different SVN working copy.");
        }

        private static bool SameUri(Uri a, Uri b)
        {
            return a != null && b != null && String.Equals(a.AbsoluteUri.TrimEnd('/'), b.AbsoluteUri.TrimEnd('/'), StringComparison.Ordinal);
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
                    "\nSubmit is not available in this milestone. Keep the files and ask a mentor; do not delete or revert them.");
        }

        private void ReconcileReadOnly(SvnClient client)
        {
            // Complete remote status first: a network failure must not grant write access.
            var entries = Status(client, Root, true, SvnDepth.Infinity);
            foreach (var item in entries.Where(x => x.Versioned && WorkspacePolicy.IsCad(x.FullPath) && File.Exists(x.FullPath)))
            {
                WorkspacePolicy.RequireInside(Root, item.FullPath);
                bool owned = WorkspacePolicy.OwnsLock(login.UserName, item.LocalLock?.Token, item.RemoteLock?.Token, item.RemoteLock?.Owner);
                FileAttributes attributes = File.GetAttributes(item.FullPath);
                File.SetAttributes(item.FullPath, owned ? attributes & ~FileAttributes.ReadOnly : attributes | FileAttributes.ReadOnly);
            }
        }

        internal long Update()
        {
            using (var client = Client())
            {
                RequireIdentity(Info(client, new SvnUriTarget(Repository)));
                WorkspacePolicy.RequireInside(Root, Root);
                if (!Directory.Exists(Path.Combine(Root, ".svn")))
                {
                    if (File.Exists(Root) || (Directory.Exists(Root) && Directory.EnumerateFileSystemEntries(Root).Any()))
                        throw new InvalidOperationException("The robot folder already contains files but is not an SVN workspace.\n" +
                            "Move your prototype/test folder to a safe backup location first. It will not be overwritten.\n" + Root);
                    Directory.CreateDirectory(Path.GetDirectoryName(Root));
                    client.CheckOut(Repository, Root, new SvnCheckOutArgs { Depth = SvnDepth.Infinity, IgnoreExternals = true, AllowObstructions = false });
                }
                else
                {
                    RequireWorkspace(client);
                    foreach (var item in Status(client, Root, false, SvnDepth.Infinity)) RequireClean(item);
                    client.Update(Root, new SvnUpdateArgs { Depth = SvnDepth.Infinity, IgnoreExternals = true, AllowObstructions = false });
                }
                RequireWorkspace(client);
                ReconcileReadOnly(client);
                return Info(client, new SvnPathTarget(Root)).Revision;
            }
        }

        internal string Edit(string path)
        {
            path = WorkspacePolicy.RequireInside(Root, path);
            if (!WorkspacePolicy.IsCad(path)) throw new InvalidOperationException("Open a saved SOLIDWORKS part, assembly, or drawing first.");
            using (var client = Client())
            {
                RequireWorkspace(client);
                var status = Status(client, path, false, SvnDepth.Empty).SingleOrDefault();
                if (status == null || !status.Versioned || status.Switched || status.IsFileExternal || status.Conflicted || status.Wedged)
                    throw new InvalidOperationException("This CAD file is not a normal version-controlled file in this workspace.");
                var local = Info(client, new SvnPathTarget(path));
                RequireIdentity(local);
                string relative = path.Substring(Root.Length + 1).Replace('\\', '/');
                Uri expected = new Uri(Repository, String.Join("/", relative.Split('/').Select(Uri.EscapeDataString)));
                if (!SameUri(local.Uri, expected)) throw new InvalidOperationException("This file was switched to another repository location.");
                var remote = Info(client, new SvnUriTarget(expected));
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
                local = Info(client, new SvnPathTarget(path));
                remote = Info(client, new SvnUriTarget(expected));
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
