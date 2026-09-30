using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using Microsoft.Win32;

namespace JocoRobos.Cad
{
    /// <summary>One SVN repository and its local folder: a robot season or the parts library.</summary>
    internal sealed class WorkspaceInfo
    {
        private const string SharedFolder = @"C:\JOCO-ROBOS";
        private static readonly Lazy<string> baseFolder = new Lazy<string>(ResolveBaseFolder);

        /// <summary>
        /// C:\JOCO-ROBOS for the Windows user who owns it (normally the only user). On a shared PC, every other
        /// Windows user gets a private %USERPROFILE%\JOCO-ROBOS, so students never share a working copy,
        /// lock tokens, or see each other's unsubmitted edits.
        /// </summary>
        internal static string BaseFolder { get { return baseFolder.Value; } }

        private static string ResolveBaseFolder()
        {
            try
            {
                var me = System.Security.Principal.WindowsIdentity.GetCurrent().User;
                string personal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "JOCO-ROBOS");
                string marker = Path.Combine(SharedFolder, ".joco-owner");
                if (File.Exists(marker))
                    return File.ReadAllText(marker).Trim() == me.Value ? SharedFolder : personal;
                if (Directory.Exists(personal) && Directory.EnumerateFileSystemEntries(personal).Any())
                    return personal;
                if (Directory.Exists(SharedFolder) && Directory.EnumerateFileSystemEntries(SharedFolder).Any())
                {
                    // Created before owners were recorded: it belongs to whoever created it.
                    if (!OwnedBy(SharedFolder, me)) return personal;
                }
                else
                    CreatePrivate(SharedFolder, me);
                File.WriteAllText(marker, me.Value);
                File.SetAttributes(marker, FileAttributes.Hidden);
                return SharedFolder;
            }
            catch (Exception exception)
            {
                System.Diagnostics.Trace.WriteLine("JOCO base folder: " + exception);
                return SharedFolder;
            }
        }

        private static bool OwnedBy(string path, System.Security.Principal.SecurityIdentifier me)
        {
#if NETFRAMEWORK
            var owner = Directory.GetAccessControl(path).GetOwner(typeof(System.Security.Principal.SecurityIdentifier)) as System.Security.Principal.SecurityIdentifier;
            return owner != null && (owner == me || owner.IsWellKnown(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid));
#else
            return true;
#endif
        }

        private static void CreatePrivate(string path, System.Security.Principal.SecurityIdentifier owner)
        {
#if NETFRAMEWORK
            // Only this student, Administrators, and SYSTEM can read it: other Windows users can't see unsubmitted work.
            var security = new System.Security.AccessControl.DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { owner,
                new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
                new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null) })
                security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(sid, System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                    System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
            Directory.CreateDirectory(path, security);
#else
            Directory.CreateDirectory(path);
#endif
        }
        internal static readonly Uri Server = new Uri("https://cad.imdad.stream/");
        internal readonly string Name;
        internal readonly Guid Id;
        internal readonly bool Archived;
        internal readonly bool IsLibrary;

        internal WorkspaceInfo(string name, Guid id, bool archived, bool library)
        {
            if (!WorkspacePolicy.IsRepositoryName(name)) throw new InvalidOperationException("The server listed an invalid robot name.");
            Name = name;
            Id = id;
            Archived = archived;
            IsLibrary = library;
        }

        internal string Root { get { return Path.Combine(BaseFolder, Name); } }
        internal Uri Repository { get { return new Uri(Server, "svn/" + Uri.EscapeDataString(Name) + "/"); } }
        internal string MasterFolder { get { return Path.Combine(Root, "00_Master"); } }
        internal string RobotPath { get { return Path.Combine(MasterFolder, "Robot.SLDASM"); } }
        internal string Label { get { return IsLibrary ? "Library" : Name; } }
        internal bool Contains(string path)
        {
            try { WorkspacePolicy.RequireInside(Root, path); return true; }
            catch (InvalidOperationException) { return false; }
        }
    }

    /// <summary>The server's list of seasons (catalog.json), maintained from the mentor web page.</summary>
    internal sealed class Catalog
    {
        [DataContract]
        private sealed class Entry
        {
            [DataMember(Name = "name")] public string Name { get; set; }
            [DataMember(Name = "uuid")] public string Uuid { get; set; }
            [DataMember(Name = "archived")] public bool Archived { get; set; }
        }

        [DataContract]
        internal sealed class AddinRelease
        {
            [DataMember(Name = "version")] public string Version { get; set; }
            [DataMember(Name = "file")] public string File { get; set; }
            [DataMember(Name = "sha256")] public string Sha256 { get; set; }
            [DataMember(Name = "required")] public bool Required { get; set; }
            [DataMember(Name = "size")] public long Size { get; set; }
        }

        [DataContract]
        private sealed class Document
        {
            [DataMember(Name = "version")] public int Version { get; set; }
            [DataMember(Name = "active")] public string Active { get; set; }
            [DataMember(Name = "robots")] public List<Entry> Robots { get; set; }
            [DataMember(Name = "library")] public Entry Library { get; set; }
            [DataMember(Name = "addin")] public AddinRelease Addin { get; set; }
        }

        private const string SettingsKey = @"Software\JOCO ROBOS\CAD";
        internal readonly List<WorkspaceInfo> Robots = new List<WorkspaceInfo>();
        internal WorkspaceInfo Library;
        internal string Active;
        internal AddinRelease Addin;

        internal static Catalog Fetch(NetworkCredential login)
        {
            var request = (HttpWebRequest)WebRequest.Create(new Uri(WorkspaceInfo.Server, "catalog.json"));
            request.Timeout = 20000;
            request.AllowAutoRedirect = false;
            request.CachePolicy = new System.Net.Cache.RequestCachePolicy(System.Net.Cache.RequestCacheLevel.NoCacheNoStore);
            request.Headers[HttpRequestHeader.Authorization] = "Basic " +
                Convert.ToBase64String(Encoding.UTF8.GetBytes(login.UserName + ":" + login.Password));
            request.UserAgent = "JOCO-ROBOS-CAD";
            try
            {
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var stream = response.GetResponseStream())
                    return Parse(stream);
            }
            catch (WebException exception) when ((exception.Response as HttpWebResponse)?.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new InvalidOperationException("The server did not accept your CAD username or password.");
            }
        }

        internal static Catalog Parse(Stream stream)
        {
            Document document;
            try { document = (Document)new DataContractJsonSerializer(typeof(Document)).ReadObject(stream); }
            catch (SerializationException) { document = null; }
            if (document == null || document.Version != 1 || document.Robots == null)
                throw new InvalidOperationException("The server's robot list is not readable. Ask a mentor, or update the add-in.");
            var catalog = new Catalog { Active = document.Active, Addin = document.Addin };
            foreach (var entry in document.Robots)
                catalog.Robots.Add(new WorkspaceInfo(entry.Name, new Guid(entry.Uuid), entry.Archived, false));
            if (document.Library != null)
                catalog.Library = new WorkspaceInfo(document.Library.Name, new Guid(document.Library.Uuid), false, true);
            return catalog;
        }

        /// <summary>Students follow the active season unless they explicitly chose another one.</summary>
        internal WorkspaceInfo Robot
        {
            get
            {
                string chosen = ChosenRobot;
                var robot = Robots.FirstOrDefault(r => r.Name == chosen) ?? Robots.FirstOrDefault(r => r.Name == Active);
                if (robot == null) throw new InvalidOperationException("No robot season is active yet. Ask a mentor.");
                return robot;
            }
        }

        internal IEnumerable<WorkspaceInfo> All
        {
            get { return Library == null ? Robots : Robots.Concat(new[] { Library }); }
        }

        internal WorkspaceInfo Owning(string path)
        {
            return All.FirstOrDefault(w => w.Contains(path));
        }

        /// <summary>Empty means "whatever mentors made active".</summary>
        internal static string ChosenRobot
        {
            get
            {
                using (var key = Registry.CurrentUser.OpenSubKey(SettingsKey))
                    return key?.GetValue("Robot") as string ?? "";
            }
            set
            {
                using (var key = Registry.CurrentUser.CreateSubKey(SettingsKey))
                    key.SetValue("Robot", value ?? "", RegistryValueKind.String);
            }
        }
    }
}
