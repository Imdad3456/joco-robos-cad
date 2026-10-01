using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace JocoRobos.Cad
{
    /// <summary>
    /// The Robot tab's file list: every SOLIDWORKS file in one robot folder, as paths relative to it. Built off the UI thread
    /// (plain file listing, never SOLIDWORKS), then searched and turned into tree nodes on demand. Pure, so it's tested directly.
    /// </summary>
    internal sealed class RobotFileIndex
    {
        internal readonly string Root;
        // Relative paths of CAD files, sorted; and each folder's direct subfolders and files (folder "" is the root).
        internal readonly List<string> Files = new List<string>();
        private readonly Dictionary<string, SortedSet<string>> folders = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> filesIn = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        private RobotFileIndex(string root) { Root = root; }

        /// <summary>Lists the robot's CAD files, skipping SVN's folder, links, SOLIDWORKS owner files (~$), and non-CAD files.</summary>
        internal static RobotFileIndex Build(string root)
        {
            var index = new RobotFileIndex(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar));
            if (Directory.Exists(index.Root)) index.Walk(index.Root, "");
            index.Files.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var list in index.filesIn.Values) list.Sort(StringComparer.OrdinalIgnoreCase);
            return index;
        }

        private void Walk(string folder, string relative)
        {
            IEnumerable<string> entries;
            try { entries = Directory.EnumerateFileSystemEntries(folder).ToList(); }
            catch (Exception) { return; } // Unreadable folder: skip it, never fail the whole list.
            bool any = false;
            foreach (string entry in entries)
            {
                string name = Path.GetFileName(entry);
                FileAttributes attributes;
                try { attributes = File.GetAttributes(entry); } catch (Exception) { continue; }
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                string child = relative.Length == 0 ? name : relative + Path.DirectorySeparatorChar + name;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (name.Equals(".svn", StringComparison.OrdinalIgnoreCase)) continue;
                    if (WalkHasCad(entry, child)) { Folder(relative).Add(name); any = true; }
                }
                else if (WorkspacePolicy.IsSubmittableCad(entry))
                {
                    Files.Add(child);
                    if (!filesIn.ContainsKey(relative)) filesIn[relative] = new List<string>();
                    filesIn[relative].Add(name);
                    any = true;
                }
            }
            if (any) Folder(relative);
        }

        // Folders without any CAD anywhere inside are left out, so the tree only shows what matters.
        private bool WalkHasCad(string folder, string relative)
        {
            int before = Files.Count;
            Walk(folder, relative);
            return Files.Count > before;
        }

        private SortedSet<string> Folder(string relative)
        {
            SortedSet<string> set;
            if (!folders.TryGetValue(relative, out set)) folders[relative] = set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            return set;
        }

        internal IEnumerable<string> SubfoldersOf(string relative)
        {
            SortedSet<string> set;
            return folders.TryGetValue(relative, out set) ? set : Enumerable.Empty<string>();
        }

        internal IEnumerable<string> FilesIn(string relative)
        {
            List<string> list;
            return filesIn.TryGetValue(relative, out list) ? list : Enumerable.Empty<string>();
        }

        internal string FullPath(string relative)
        {
            return Path.Combine(Root, relative);
        }

        /// <summary>Files whose name or folder contains every word typed (case-insensitive), names that match first.</summary>
        internal List<string> Search(string query, int limit = 200)
        {
            var words = (query ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(w => w.ToLowerInvariant()).ToArray();
            if (words.Length == 0) return new List<string>();
            return Files.Where(f => Matches(f, words))
                .OrderBy(f => words.All(w => Path.GetFileName(f).ToLowerInvariant().Contains(w)) ? 0 : 1)
                .ThenBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .Take(limit).ToList();
        }

        internal static bool Matches(string relative, string[] words)
        {
            string text = relative.ToLowerInvariant();
            return words.All(text.Contains);
        }
    }
}
