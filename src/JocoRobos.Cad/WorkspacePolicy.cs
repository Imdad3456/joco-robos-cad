using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace JocoRobos.Cad
{
    internal static class WorkspacePolicy
    {
        internal static bool IsCad(string path)
        {
            string ext = Path.GetExtension(path);
            return ext.Equals(".sldprt", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".sldasm", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".slddrw", StringComparison.OrdinalIgnoreCase);
        }

        // Matches the server's names: a season like 2028-Robot, or the shared Library.
        internal static bool IsRepositoryName(string name)
        {
            return name != null && (name == "Library" || Regex.IsMatch(name, @"^(19|20)\d{2}-Robot$"));
        }

        // Library parts are copied into the robot under 90_COTS, mirroring the library's folders.
        // One fixed location per library file means a part used twice is copied once, and SOLIDWORKS
        // never sees two different files with the same name.
        internal static string LibraryCopyPath(string libraryRoot, string robotRoot, string libraryFile)
        {
            string file = RequireInside(libraryRoot, libraryFile);
            string relative = file.Substring(Path.GetFullPath(libraryRoot).TrimEnd(Path.DirectorySeparatorChar).Length + 1);
            return Path.Combine(Path.GetFullPath(robotRoot), "90_COTS", relative);
        }

        // SOLIDWORKS keeps imported (3D Interconnect) and session data under the Windows temp folder.
        /// <summary>A virtual component, saved inside its assembly: SOLIDWORKS names them "Name^Assembly.SLDPRT".</summary>
        internal static bool IsVirtualComponent(string path)
        {
            return !String.IsNullOrEmpty(path) && Path.GetFileName(path).IndexOf('^') > 0;
        }

        internal static bool IsTemporary(string path, string tempRoot)
        {
            try
            {
                string full = Path.GetFullPath(path);
                string temp = Path.GetFullPath(tempRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                return full.StartsWith(temp, StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException) { return false; }
        }

        /// <summary>A folder name that doesn't exist yet: the timestamp to the second, then " (2)", " (3)"… if needed.</summary>
        internal static string UniqueFolder(string parent, DateTime now)
        {
            string stamp = now.ToString("yyyy-MM-dd HHmmss", System.Globalization.CultureInfo.InvariantCulture);
            string candidate = Path.Combine(parent, stamp);
            for (int i = 2; Directory.Exists(candidate) || File.Exists(candidate); i++)
                candidate = Path.Combine(parent, stamp + " (" + i + ")");
            return candidate;
        }

        // Windows and SOLIDWORKS can misbehave near the classic 260-character path limit; leave room for SVN and temp files.
        internal const int MaxPath = 240;
        internal static bool TooLong(string path) { return path != null && path.Length > MaxPath; }

        // SOLIDWORKS owner/lock files ("~$Part.SLDPRT") share CAD extensions but are never submitted.
        internal static bool IsSubmittableCad(string path)
        {
            return IsCad(path) && !IsOwnerFile(path);
        }

        // A submit that converted files to the current SOLIDWORKS format: Upgrade Robot Files' own, or one finished by hand
        // after an interrupted upgrade ("Convert files to SOLIDWORKS 2026").
        internal static bool IsConversionMessage(string message)
        {
            message = message ?? "";
            return message.IndexOf("Upgrade Robot Files", StringComparison.OrdinalIgnoreCase) >= 0 ||
                (message.IndexOf("convert", StringComparison.OrdinalIgnoreCase) >= 0 && message.IndexOf("solidworks", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // Left behind when SOLIDWORKS closes unexpectedly; never team work, so Update steps around them.
        internal static bool IsOwnerFile(string path)
        {
            return Path.GetFileName(path ?? "").StartsWith("~$", StringComparison.Ordinal);
        }

        // Unversioned folders between the file and the nearest versioned folder, outermost first.
        internal static List<string> UnversionedParents(string root, string file, Func<string, bool> isVersioned)
        {
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            var parents = new List<string>();
            string cursor = Path.GetDirectoryName(Path.GetFullPath(file));
            while (cursor != null && cursor.Length > fullRoot.Length && !isVersioned(cursor))
            {
                parents.Insert(0, cursor);
                cursor = Path.GetDirectoryName(cursor);
            }
            if (cursor == null || cursor.Length < fullRoot.Length)
                throw new InvalidOperationException("This file is outside the robot workspace.");
            return parents;
        }

        internal static string RequireComment(string comment)
        {
            comment = (comment ?? "").Trim();
            if (comment.Length < 3) throw new InvalidOperationException("Describe what you changed before submitting.");
            return comment;
        }

        internal static string RequireInside(string root, string path)
        {
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            string full = Path.GetFullPath(path);
            string prefix = fullRoot + Path.DirectorySeparatorChar;
            if (!full.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) &&
                !full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("This file is outside the robot workspace.");
            string relative = full.Length == fullRoot.Length ? "" : full.Substring(prefix.Length);
            foreach (string segment in relative.Split(Path.DirectorySeparatorChar))
                if (segment.Equals(".svn", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("That's JOCO's own bookkeeping folder, not CAD.");
            // Reject junctions/symlinks, including the workspace's ancestors.
            string cursor = full;
            while (!String.IsNullOrEmpty(cursor))
            {
                if ((File.Exists(cursor) || Directory.Exists(cursor)) &&
                    (File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Use a local workspace without links or junctions.");
                cursor = Path.GetDirectoryName(cursor);
            }
            return full;
        }

        /// <summary>SOLIDWORKS' revision number ("34.1.0") as its version year (2026); 0 if unknown.</summary>
        internal static int SolidWorksYear(string revisionNumber)
        {
            int major;
            string first = (revisionNumber ?? "").Split('.')[0];
            return Int32.TryParse(first, out major) && major >= 20 && major < 100 ? 1992 + major : 0;
        }

        /// <summary>
        /// Why this SOLIDWORKS must not change team CAD, or null. A newer SOLIDWORKS saves files the team's version can't open;
        /// an older one can't open what the team saved. No approved version (or an unknown one here) means no restriction.
        /// </summary>
        internal static string SolidWorksProblem(int mine, string approved)
        {
            int team;
            if (!Int32.TryParse(approved ?? "", out team) || team == mine) return null;
            if (mine == 0)
                return "JOCO couldn't tell which SOLIDWORKS version this is, and the team uses SOLIDWORKS " + team + ". To be safe you can look at the robot " +
                    "but not edit or submit it. Restart SOLIDWORKS; if this stays, Copy Diagnostics and send it to a mentor.";
            return mine > team
                ? "This computer has SOLIDWORKS " + mine + ", but the team uses SOLIDWORKS " + team + ". Files saved here couldn't be opened by everyone else, " +
                  "so you can look at the robot but not edit or submit it. Use SOLIDWORKS " + team + ", or ask a mentor (the team upgrades together)."
                : "This computer has SOLIDWORKS " + mine + ", but the team uses SOLIDWORKS " + team + ". Update SOLIDWORKS to " + team + " to edit and submit.";
        }

        /// <summary>A SOLIDWORKS file (not an owner/lock file) inside this robot folder: what the Robot tab may open.</summary>
        internal static bool IsRobotFile(string root, string path)
        {
            if (String.IsNullOrEmpty(root) || String.IsNullOrEmpty(path) || !IsSubmittableCad(path)) return false;
            try { RequireInside(root, path); return true; }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// A team server address as typed by a student ("cad.example.org" or "https://cad.example.org/"), normalized to
        /// https://host[:port]/. Only HTTPS: the password goes with every request.
        /// </summary>
        internal static bool TryServerAddress(string text, out Uri server)
        {
            server = null;
            text = (text ?? "").Trim();
            if (text.Length == 0 || text.Any(Char.IsWhiteSpace)) return false;
            if (!text.Contains("://")) text = "https://" + text;
            Uri parsed;
            if (!Uri.TryCreate(text, UriKind.Absolute, out parsed) || parsed.Scheme != Uri.UriSchemeHttps || parsed.HostNameType != UriHostNameType.Dns ||
                !parsed.Host.Contains(".") || parsed.UserInfo.Length > 0 || parsed.Query.Length > 0 || parsed.Fragment.Length > 0 ||
                parsed.AbsolutePath.TrimEnd('/').Length > 0) return false;
            server = new Uri("https://" + parsed.Host.ToLowerInvariant() + (parsed.IsDefaultPort ? "" : ":" + parsed.Port) + "/");
            return true;
        }

        /// <summary>True when a robot copy points at an earlier address of this same server (same repository path).</summary>
        internal static bool IsOldAddress(Uri current, Uri expected, IEnumerable<string> oldHosts)
        {
            if (current == null || expected == null || !String.Equals(current.Scheme, expected.Scheme, StringComparison.OrdinalIgnoreCase)) return false;
            return oldHosts.Any(h => String.Equals(h, current.Host, StringComparison.OrdinalIgnoreCase)) &&
                String.Equals(current.AbsolutePath.TrimEnd('/'), expected.AbsolutePath.TrimEnd('/'), StringComparison.Ordinal);
        }

        internal static bool OwnsLock(string user, string localToken, string remoteToken, string owner)
        {
            return !String.IsNullOrEmpty(localToken) &&
                String.Equals(localToken, remoteToken, StringComparison.Ordinal) &&
                String.Equals(user, owner, StringComparison.Ordinal);
        }
    }
}
