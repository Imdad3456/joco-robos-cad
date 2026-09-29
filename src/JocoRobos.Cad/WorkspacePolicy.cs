using System;
using System.IO;

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
