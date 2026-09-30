using System;
using System.Collections.Generic;
using System.IO;
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
            return IsCad(path) && !Path.GetFileName(path).StartsWith("~$", StringComparison.Ordinal);
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
                    throw new InvalidOperationException("SVN metadata cannot be edited as CAD.");
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

        internal static bool OwnsLock(string user, string localToken, string remoteToken, string owner)
        {
            return !String.IsNullOrEmpty(localToken) &&
                String.Equals(localToken, remoteToken, StringComparison.Ordinal) &&
                String.Equals(user, owner, StringComparison.Ordinal);
        }
    }
}
