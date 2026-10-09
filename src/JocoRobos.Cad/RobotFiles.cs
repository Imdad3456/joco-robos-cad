using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace JocoRobos.Cad
{
    internal enum FileMark { None, Mine, MineElsewhere, Theirs, New, Incoming, Changed }

    /// <summary>How one robot file shows in the tree: a mark, a short word after the name, and the full story on hover.</summary>
    internal sealed class FileStatus
    {
        internal FileMark Mark;
        internal string Detail = "";
        internal string Tip = "";
        internal string Owner;      // who to ask for it (Theirs only)
    }

    /// <summary>What's going on inside a folder, so ownership shows without expanding it.</summary>
    internal sealed class FolderSummary
    {
        internal int Mine, Theirs, Incoming, New;

        internal string Text
        {
            get
            {
                var parts = new List<string>();
                if (Mine > 0) parts.Add("✎" + Mine);
                if (Theirs > 0) parts.Add("🔒" + Theirs);
                if (New > 0) parts.Add("●" + New);
                if (Incoming > 0) parts.Add("⬇" + Incoming);
                return String.Join("  ", parts);
            }
        }
    }

    /// <summary>Robot file statuses from the last status check (no extra server request). Pure, so it's tested directly.</summary>
    internal static class RobotFileStatus
    {
        internal static string Since(DateTime since, DateTime now)
        {
            return since.Date == now.Date ? since.ToString("h:mm tt") : since.ToString("ddd MMM d, h:mm tt");
        }

        internal static FileStatus Of(WorkspaceSnapshot snapshot, string full, string user, DateTime now)
        {
            var status = new FileStatus();
            if (snapshot == null) return status;
            string owner, who;
            DateTime since;
            string when = snapshot.LockedSince.TryGetValue(full, out since) ? " since " + Since(since, now) : "";
            bool incoming = snapshot.IncomingFiles.TryGetValue(full, out who);
            string newer = incoming ? "\n" + who + " submitted a newer version; it comes in with the next update." : "";
            if (snapshot.Mine.Contains(full))
            {
                status.Mark = FileMark.Mine;
                status.Detail = snapshot.Changed.Contains(full) ? "you · not submitted" : "you";
                status.Tip = "You're editing this" + when + (snapshot.Changed.Contains(full) ? ". Your changes aren't submitted yet." : ".");
            }
            else if (snapshot.Locks.TryGetValue(full, out owner) && owner == user)
            {
                status.Mark = FileMark.MineElsewhere;
                status.Detail = "you, other computer";
                status.Tip = "You're editing this on another computer" + when + ". Submit it there, or ask a mentor to release it.";
            }
            else if (snapshot.Locks.TryGetValue(full, out owner))
            {
                status.Mark = FileMark.Theirs;
                status.Owner = owner;
                status.Detail = owner;
                status.Tip = owner + " is editing this" + when + ". Right-click → Ask " + owner + " for it." + newer;
            }
            else if (snapshot.New.Contains(full))
            {
                status.Mark = FileMark.New;
                status.Detail = "new · not submitted";
                status.Tip = "New file: it goes to the team with your next Submit.";
            }
            else if (incoming)
            {
                status.Mark = FileMark.Incoming;
                status.Detail = "newer from " + who;
                status.Tip = newer.TrimStart('\n');
            }
            else if (snapshot.Changed.Contains(full))
            {
                status.Mark = FileMark.Changed;
                status.Detail = "changed here";
                status.Tip = "Changed on this computer but not locked by you, so it can't be submitted. Tools → CAD Hub → Set Aside My Changes keeps a copy.";
            }
            return status;
        }

        internal static FolderSummary Summarize(WorkspaceSnapshot snapshot, string folderFull, string user)
        {
            var summary = new FolderSummary();
            if (snapshot == null) return summary;
            string prefix = folderFull.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Func<string, bool> inside = p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
            summary.Mine = snapshot.Mine.Count(inside);
            summary.Theirs = snapshot.Locks.Count(l => inside(l.Key) && l.Value != user);
            summary.New = snapshot.New.Count(inside);
            summary.Incoming = snapshot.IncomingFiles.Keys.Count(p => inside(p) && WorkspacePolicy.IsSubmittableCad(p));
            return summary;
        }
    }

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
