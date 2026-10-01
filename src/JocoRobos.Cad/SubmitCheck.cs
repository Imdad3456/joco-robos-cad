using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace JocoRobos.Cad
{
    internal enum SubmitKind { Modified, New, ReleaseOnly }

    internal sealed class SubmitItem
    {
        internal SubmitKind Kind;
        internal string Path;
        internal WorkspaceInfo Workspace;
        // Changed without Edit, but free and current: Submit locks it first.
        internal bool NeedsLock;
        // Why a changed or missing file can't be submitted (Set Aside and Restore lists).
        internal string Reason;
        internal string Relative { get { return Path.Substring(Workspace.Root.Length + 1); } }
        internal string Name { get { return System.IO.Path.GetFileName(Path); } }
        /// <summary>The containing folder for display, e.g. "30_Shooter" or "Library\REV".</summary>
        internal string Folder
        {
            get
            {
                string folder = System.IO.Path.GetDirectoryName(Relative) ?? "";
                if (Workspace.IsLibrary) return folder.Length == 0 ? "Library" : "Library\\" + folder;
                return folder.Length == 0 ? "(robot folder)" : folder;
            }
        }
        public override string ToString()
        {
            string label = Kind == SubmitKind.Modified ? "Modified" : Kind == SubmitKind.New ? "New" : "Unchanged — release lock";
            return label + ":  " + (Workspace.IsLibrary ? "Library\\" : "") + Relative + (NeedsLock ? "   (not locked yet; Submit locks it)" : "");
        }
    }

    internal sealed class SubmitPlan
    {
        internal readonly List<SubmitItem> Items = new List<SubmitItem>();
        // Problems only a mentor can repair.
        internal readonly List<string> Blocked = new List<string>();
        // Changed files that can't be submitted (someone else's lock, or a newer server version): Set Aside can move them out of the way.
        internal readonly List<SubmitItem> SetAside = new List<SubmitItem>();
        // Deleted or missing team files that Restore Deleted Files can bring back.
        internal readonly List<SubmitItem> Restore = new List<SubmitItem>();
        internal string Notice;

        internal void Add(SubmitPlan part)
        {
            Items.AddRange(part.Items);
            Blocked.AddRange(part.Blocked);
            SetAside.AddRange(part.SetAside);
            Restore.AddRange(part.Restore);
            if (part.Notice != null) Notice = (Notice == null ? "" : Notice + "\n\n") + part.Notice;
        }
    }

    internal enum IssueAction { SaveDocuments, LockFile, ImportIntoRobot, IncludeFile, RestoreFiles, ShowFile, ShowOther, SubmitAnyway }

    internal enum IssueLevel { Blocking, Warning, Info }

    /// <summary>One problem in the Submit window: what's wrong, which files, and the buttons that fix it.</summary>
    internal sealed class SubmitIssue
    {
        internal IssueLevel Level;
        internal string Key;
        internal string Title;
        internal string Description;
        // The files an action works on (documents to save, the assembly to import into, the file to include…).
        internal readonly List<string> Files = new List<string>();
        // A second file worth showing: the existing duplicate, or the outside file.
        internal string Other;
        internal readonly List<IssueAction> Actions = new List<IssueAction>();
        internal readonly List<SubmitItem> Items = new List<SubmitItem>();
        internal bool Blocking { get { return Level == IssueLevel.Blocking; } }
        public override string ToString() { return Level + ": " + Title; }
    }

    /// <summary>What SOLIDWORKS has open, read by the caller; Path is null for a document that was never saved.</summary>
    internal sealed class OpenDocument
    {
        internal string Path;
        internal string Title;
        internal bool Dirty;
        internal bool ReadOnly;
    }

    internal sealed class SubmitCheckInput
    {
        internal SubmitPlan Plan;
        internal ISet<string> Selected;
        internal IList<WorkspaceInfo> Workspaces;
        internal IList<OpenDocument> Documents = new List<OpenDocument>();
        // Stored reference paths of a saved CAD file, all levels deep.
        internal Func<string, IEnumerable<string>> References = path => Enumerable.Empty<string>();
        // The same for the team's version of a changed file (what this computer last got from the server). A reference that was
        // already missing or temporary there isn't this student's doing, so it never blocks their Submit.
        internal Func<string, IEnumerable<string>> TeamReferences = path => Enumerable.Empty<string>();
        // Which workspace (any season or the Library) a path belongs to, or null.
        internal Func<string, WorkspaceInfo> Owning = path => null;
        // From the last status check: file → who holds its lock.
        internal IDictionary<string, string> LockedBy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        internal string User;
        internal ISet<string> Acknowledged = new HashSet<string>();
        internal string TempFolder = System.IO.Path.GetTempPath();
    }

    /// <summary>
    /// Everything Submit can know before committing, as structured issues. An early warning only:
    /// SvnWorkspace.Submit and the server hooks still enforce the rules when the files are committed.
    /// Pure except for reading the file system, so it runs without SOLIDWORKS in tests.
    /// </summary>
    internal static class SubmitCheck
    {
        internal const string TemporaryKey = "temporary-references";

        internal static List<SubmitIssue> Run(SubmitCheckInput input)
        {
            var issues = new List<SubmitIssue>();
            var plan = input.Plan;
            var selected = plan.Items.Where(x => input.Selected.Contains(x.Path)).ToList();
            Func<string, WorkspaceInfo> submitting = path => input.Workspaces.FirstOrDefault(w => w.Contains(path));

            // Unsaved documents: only the robot's and Library's own count; unrelated open files are left alone.
            var save = new List<string>();
            foreach (var doc in input.Documents.Where(d => d.Dirty))
            {
                if (String.IsNullOrEmpty(doc.Path))
                {
                    issues.Add(new SubmitIssue { Level = IssueLevel.Warning, Key = "never-saved:" + doc.Title,
                        Title = doc.Title + " has never been saved",
                        Description = "If it belongs to the robot, use File → Save As into your robot folder and it will be included. Otherwise ignore this." });
                    continue;
                }
                if (submitting(doc.Path) == null || !WorkspacePolicy.IsCad(doc.Path)) continue;
                if (!doc.ReadOnly) { save.Add(doc.Path); continue; }
                // Loading and rebuilding marks assemblies changed by themselves; only parts and drawings are worth mentioning.
                if (doc.Path.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase)) continue;
                string owner;
                if (input.LockedBy.TryGetValue(doc.Path, out owner) && owner != input.User)
                {
                    issues.Add(new SubmitIssue { Level = IssueLevel.Warning, Key = "locked:" + doc.Path,
                        Title = "Unsaved changes in " + Path.GetFileName(doc.Path) + " can't be submitted",
                        Description = owner + " is editing it. Undo them, or use File → Save As to keep a copy outside the robot folder.",
                        Files = { doc.Path } });
                    continue;
                }
                issues.Add(new SubmitIssue { Level = IssueLevel.Warning, Key = "readonly:" + doc.Path,
                    Title = "Unsaved changes in " + Path.GetFileName(doc.Path),
                    Description = "It's read-only, so those changes won't be submitted. Lock it to keep them (you'll save it next), or ignore this if you didn't mean to change it.",
                    Files = { doc.Path }, Actions = { IssueAction.LockFile } });
            }
            if (save.Count > 0)
            {
                var issue = new SubmitIssue { Level = IssueLevel.Blocking, Key = "save",
                    Title = save.Count == 1 ? "1 document needs to be saved before submitting" : save.Count + " documents need to be saved before submitting",
                    Description = String.Join("\n", save.Select(Path.GetFileName)), Actions = { IssueAction.SaveDocuments } };
                issue.Files.AddRange(save);
                issues.Add(issue);
            }

            // Windows and SOLIDWORKS get unreliable near 260 characters.
            foreach (var item in selected.Where(x => x.Kind == SubmitKind.New && WorkspacePolicy.TooLong(x.Path)))
                issues.Add(new SubmitIssue { Level = IssueLevel.Blocking, Key = "long:" + item.Path,
                    Title = item.Name + ": path is too long",
                    Description = item.Relative + "\nIt's over " + WorkspacePolicy.MaxPath + " characters. Use a shorter folder or file name (File → Save As); this check clears by itself.",
                    Files = { item.Path }, Actions = { IssueAction.ShowFile } });

            // SOLIDWORKS mixes up different files with the same name, even in different folders.
            var indexes = new Dictionary<string, ILookup<string, string>>();
            Func<WorkspaceInfo, ILookup<string, string>> names = w =>
            {
                ILookup<string, string> index;
                if (!indexes.TryGetValue(w.Name, out index)) indexes[w.Name] = index = CadByName(w.Root);
                return index;
            };
            foreach (var item in selected.Where(x => x.Kind == SubmitKind.New))
            {
                var others = names(item.Workspace)[item.Name].Where(p => !p.Equals(item.Path, StringComparison.OrdinalIgnoreCase)).ToList();
                if (others.Count == 0) continue;
                issues.Add(new SubmitIssue { Level = IssueLevel.Blocking, Key = "duplicate:" + item.Path,
                    Title = item.Name + " has the same name as another file",
                    Description = "Yours:  " + item.Relative + "\nExisting:  " + String.Join(", ", others.Take(3).Select(p => p.Substring(item.Workspace.Root.Length + 1))) +
                        "\nSOLIDWORKS can't tell apart files with the same name. Rename yours with File → Save As (for example " +
                        Path.GetFileNameWithoutExtension(item.Name) + "_2" + Path.GetExtension(item.Name) + "), use the new name in the assembly, and save. This check clears by itself.",
                    Files = { item.Path }, Other = others[0], Actions = { IssueAction.ShowFile, IssueAction.ShowOther } });
            }

            // Teammates must be able to open what is submitted: every reference inside the same workspace
            // (library parts are copied in, never linked), and no new files left behind on this computer.
            var newFiles = new HashSet<string>(plan.Items.Where(x => x.Kind == SubmitKind.New).Select(x => x.Path), StringComparer.OrdinalIgnoreCase);
            var needed = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var temporary = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in selected.Where(x => x.Kind != SubmitKind.ReleaseOnly && WorkspacePolicy.IsCad(x.Path)))
            {
                var outside = new List<string>();
                var library = new List<string>();
                var missing = new List<string>();
                var before = new HashSet<string>(item.Kind == SubmitKind.Modified ? input.TeamReferences(item.Path) ?? Enumerable.Empty<string>()
                    : Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
                foreach (string reference in input.References(item.Path).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    // Virtual components ("Belt1^Robot.SLDPRT") are saved inside their assembly: SOLIDWORKS only unpacks them into
                    // its temp folder while the assembly is open. Nothing to copy, nothing missing.
                    if (WorkspacePolicy.IsVirtualComponent(reference)) continue;
                    string resolved = Resolve(reference, item.Path, names(item.Workspace), input.TempFolder);
                    if (resolved == null)
                    {
                        if (before.Contains(reference)) continue; // Already like this in the team's version.
                        // Imported (3D Interconnect) or virtual data that only lives in SOLIDWORKS' temp folder.
                        if (WorkspacePolicy.IsTemporary(reference, input.TempFolder)) temporary.Add(Path.GetFileName(reference));
                        // Not anywhere SOLIDWORKS will look: teammates would see it missing. Only CAD files: design tables,
                        // decals, and similar aren't synced by JOCO (a documented limit).
                        else if (WorkspacePolicy.IsCad(reference)) missing.Add(reference);
                        continue;
                    }
                    if (!item.Workspace.Contains(resolved))
                    {
                        var other = input.Owning(resolved);
                        (other != null && other.IsLibrary ? library : outside).Add(resolved);
                    }
                    else if (newFiles.Contains(resolved) && !input.Selected.Contains(resolved))
                    {
                        List<string> users;
                        if (!needed.TryGetValue(resolved, out users)) needed[resolved] = users = new List<string>();
                        users.Add(item.Name);
                    }
                }
                // The key names the actual files, so "Submit anyway" never covers a different problem found later.
                string missingKey = "missing:" + item.Path + ":" + String.Join("|", missing.Select(p => p.ToLowerInvariant()).OrderBy(p => p));
                if (missing.Count > 0 && !input.Acknowledged.Contains(missingKey))
                    issues.Add(new SubmitIssue { Level = IssueLevel.Blocking, Key = missingKey,
                        Title = item.Name + " uses " + (missing.Count == 1 ? "a file that isn't" : missing.Count + " files that aren't") + " on this computer",
                        Description = String.Join("\n", missing.Take(4).Select(p => "   " + Path.GetFileName(p) + "  (was at " + Path.GetDirectoryName(p) + ")")) +
                            (missing.Count > 4 ? "\n   …and " + (missing.Count - 4) + " more" : "") +
                            "\nTeammates would see " + (missing.Count == 1 ? "it" : "them") + " as missing too. Find the file and copy it into the robot (File → Open " + item.Name +
                            " shows what SOLIDWORKS can't find), or remove that component. If it really isn't needed, Submit anyway.",
                        Files = { item.Path }, Actions = { IssueAction.SubmitAnyway } });
                if (outside.Count + library.Count == 0) continue;
                bool importable = !item.Workspace.IsLibrary && item.Path.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase);
                var lines = outside.Take(4).Select(p => "   " + Path.GetFileName(p) + "  (" + Path.GetDirectoryName(p) + ")")
                    .Concat(library.Take(4).Select(p => "   " + Path.GetFileName(p) + "  (linked straight to the Library)")).ToList();
                if (outside.Count + library.Count > lines.Count) lines.Add("   …and " + (outside.Count + library.Count - lines.Count) + " more");
                var issue = new SubmitIssue { Level = IssueLevel.Blocking, Key = "outside:" + item.Path,
                    Title = item.Name + " uses " + (outside.Count + library.Count == 1 ? "a file" : "files") + " outside " + item.Workspace.Label,
                    Description = String.Join("\n", lines) + "\n" + (importable
                        ? "Teammates wouldn't be able to open it. Import copies them into the robot and points the assembly at the copies."
                        : "Teammates wouldn't be able to open it. Copy the file into " + item.Workspace.Label + " and point " + item.Name + " at the copy (File → Find References)."),
                    Files = { item.Path }, Other = outside.Concat(library).First() };
                if (importable) issue.Actions.Add(IssueAction.ImportIntoRobot);
                issue.Actions.Add(IssueAction.ShowOther);
                issues.Add(issue);
            }
            foreach (var pair in needed)
                issues.Add(new SubmitIssue { Level = IssueLevel.Blocking, Key = "needs:" + pair.Key,
                    Title = String.Join(", ", pair.Value.Distinct().Take(3)) + " needs " + Path.GetFileName(pair.Key),
                    Description = Path.GetFileName(pair.Key) + " is new and unchecked, so teammates couldn't open " + (pair.Value.Count == 1 ? "it" : "them") + " without it.",
                    Files = { pair.Key }, Actions = { IssueAction.IncludeFile } });
            string temporaryKey = TemporaryKey + ":" + String.Join("|", temporary.Select(p => p.ToLowerInvariant()));
            if (temporary.Count > 0 && !input.Acknowledged.Contains(temporaryKey))
                issues.Add(new SubmitIssue { Level = IssueLevel.Blocking, Key = temporaryKey,
                    Title = temporary.Count + " imported part(s) exist only in SOLIDWORKS' temporary folder",
                    Description = String.Join(", ", temporary.Take(4)) + (temporary.Count > 4 ? ", …" : "") +
                        "\nThis happens with parts inserted from STEP or other CAD formats. Teammates may see them as missing. To make them permanent: " +
                        "right-click each imported part → Break Link, or open it and Save As into the robot folder.",
                    Actions = { IssueAction.SubmitAnyway } });

            // Files that stay behind: shown so nothing is silently skipped, but they don't stop the rest.
            if (plan.Restore.Count > 0)
            {
                var issue = new SubmitIssue { Level = IssueLevel.Warning, Key = "missing",
                    Title = plan.Restore.Count == 1 ? plan.Restore[0].Name + " is missing on this computer" : plan.Restore.Count + " team files are missing on this computer",
                    Description = String.Join("\n", plan.Restore.Take(6).Select(x => x.Relative)) + (plan.Restore.Count > 6 ? "\n…" : "") +
                        "\nIf they were deleted by accident, Restore brings back the team's copy. Renaming or removing team CAD is a mentor task.",
                    Actions = { IssueAction.RestoreFiles } };
                issue.Items.AddRange(plan.Restore);
                issues.Add(issue);
            }
            foreach (var item in plan.SetAside)
                issues.Add(new SubmitIssue { Level = IssueLevel.Warning, Key = "held:" + item.Path,
                    Title = item.Name + " can't be submitted", Description = item.Reason ?? "", Files = { item.Path }, Actions = { IssueAction.ShowFile } });
            if (plan.Blocked.Count > 0)
                issues.Add(new SubmitIssue { Level = IssueLevel.Warning, Key = "blocked",
                    Title = "Needs a mentor", Description = String.Join("\n", plan.Blocked.Take(8)) + (plan.Blocked.Count > 8 ? "\n…" : "") });
            return issues.OrderBy(x => x.Level).ToList();
        }

        /// <summary>
        /// Where SOLIDWORKS will actually find a reference: the stored path if it exists (outside the temp folder),
        /// otherwise a file with the same name, preferring the referencing document's own folder.
        /// </summary>
        internal static string Resolve(string reference, string referencing, ILookup<string, string> index, string tempFolder)
        {
            if (File.Exists(reference) && !WorkspacePolicy.IsTemporary(reference, tempFolder)) return Path.GetFullPath(reference);
            var matches = index[Path.GetFileName(reference)].ToList();
            string folder = Path.GetDirectoryName(referencing);
            return matches.FirstOrDefault(m => String.Equals(Path.GetDirectoryName(m), folder, StringComparison.OrdinalIgnoreCase)) ?? matches.FirstOrDefault();
        }

        internal static ILookup<string, string> CadByName(string root)
        {
            return CadFiles(root).ToLookup(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);
        }

        // Every CAD file under a folder, skipping SVN's own folder and links.
        private static IEnumerable<string> CadFiles(string folder)
        {
            if (!Directory.Exists(folder)) yield break;
            foreach (string file in Directory.EnumerateFiles(folder))
                if (WorkspacePolicy.IsSubmittableCad(file)) yield return file;
            foreach (string child in Directory.EnumerateDirectories(folder))
            {
                if (Path.GetFileName(child).Equals(".svn", StringComparison.OrdinalIgnoreCase) ||
                    (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                foreach (string file in CadFiles(child)) yield return file;
            }
        }

        /// <summary>The Submit button: files committed, or locks released when nothing changed.</summary>
        internal static string SubmitLabel(IEnumerable<SubmitItem> selected)
        {
            var list = selected.ToList();
            int files = list.Count(x => x.Kind != SubmitKind.ReleaseOnly);
            if (files > 0) return "Submit " + files;
            return list.Count > 0 ? "Release " + list.Count : "Submit";
        }
    }
}
