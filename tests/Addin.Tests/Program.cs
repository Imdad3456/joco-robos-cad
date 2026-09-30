// Add-in unit checks that run without SOLIDWORKS or Windows: path safety, lock ownership, file naming,
// the season catalog, update offers, library copy rules, and the Submit window's checks. Run: dotnet run --project tests/Addin.Tests
// (CI runs this on every push). SOLIDWORKS behavior itself is tested by hand with TESTING.md.
using System;
using System.IO;
using System.Linq;
using JocoRobos.Cad;

static class Program
{
    private static int assertions;
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        assertions++;
    }

    static void Denied(Action action, string message)
    {
        try { action(); }
        catch (InvalidOperationException) { assertions++; return; }
        throw new Exception(message);
    }

    static void Main()
    {
        string temp = Path.Combine(Path.GetTempPath(), "joco-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            string root = Path.Combine(temp, "Robot");
            Directory.CreateDirectory(root);
            string part = Path.Combine(root, "Intake", "Plate.SLDPRT");
            Check(WorkspacePolicy.RequireInside(root, part) == part, "Normal CAD path refused");
            Denied(() => WorkspacePolicy.RequireInside(root, Path.Combine(temp, "Robot-other", "Plate.SLDPRT")), "Sibling-prefix bypass");
            Denied(() => WorkspacePolicy.RequireInside(root, Path.Combine(root, "..", "outside.SLDPRT")), "Traversal bypass");
            Denied(() => WorkspacePolicy.RequireInside(root, Path.Combine(root, ".svn", "metadata.SLDPRT")), "Metadata bypass");
            if (!OperatingSystem.IsWindows())
            {
                Directory.CreateSymbolicLink(Path.Combine(root, "linked"), temp);
                Denied(() => WorkspacePolicy.RequireInside(root, Path.Combine(root, "linked", "outside.SLDPRT")), "Symlink bypass");
            }
            Check(WorkspacePolicy.IsCad("Part.sLdPrT"), "Mixed-case CAD extension rejected");
            Check(!WorkspacePolicy.IsCad("Part.SLDPRT.bak"), "Backup misidentified as CAD");
            Check(WorkspacePolicy.OwnsLock("sarah", "token-1", "token-1", "sarah"), "Valid lock rejected");
            Check(!WorkspacePolicy.OwnsLock("sarah", null, "token-1", "sarah"), "Same username without local token accepted");
            Check(!WorkspacePolicy.OwnsLock("sarah", "token-1", "token-2", "sarah"), "Stale token accepted");
            Check(!WorkspacePolicy.OwnsLock("sarah", "token-1", null, null), "Missing server lock accepted");
            Check(!WorkspacePolicy.OwnsLock("imdad", "token-1", "token-1", "sarah"), "Other user's lock accepted");
            Check(WorkspacePolicy.IsSubmittableCad(Path.Combine(root, "CameraMount.SLDPRT")), "New part not submittable");
            Check(!WorkspacePolicy.IsSubmittableCad(Path.Combine(root, "~$CameraMount.SLDPRT")), "SOLIDWORKS owner file submittable");
            Check(WorkspacePolicy.IsConversionMessage("Convert all files to the current SOLIDWORKS format (Upgrade Robot Files)") &&
                WorkspacePolicy.IsConversionMessage("convert files to solidworks 2026 (part 1)") && !WorkspacePolicy.IsConversionMessage("Converted intake to hex"), "Conversion submit detection");
            Check(WorkspacePolicy.IsOwnerFile(Path.Combine(root, "00_Master", "~$Robot.SLDASM")) && !WorkspacePolicy.IsOwnerFile(Path.Combine(root, "Robot.SLDASM")), "Leftover lock file not recognized");
            Check(!WorkspacePolicy.IsSubmittableCad(Path.Combine(root, "notes.txt")), "Non-CAD submittable");
            string shooter = Path.Combine(root, "30_Shooter");
            string nested = Path.Combine(shooter, "Camera", "Mounts", "Bracket.SLDPRT");
            var versioned = new System.Collections.Generic.HashSet<string> { root, shooter };
            var parents = WorkspacePolicy.UnversionedParents(root, nested, versioned.Contains);
            Check(parents.Count == 2 && parents[0] == Path.Combine(shooter, "Camera") && parents[1] == Path.Combine(shooter, "Camera", "Mounts"), "New parent folders wrong or unordered");
            Check(WorkspacePolicy.UnversionedParents(root, Path.Combine(shooter, "Plate.SLDPRT"), versioned.Contains).Count == 0, "Versioned folder scheduled for add");
            Denied(() => WorkspacePolicy.UnversionedParents(root, Path.Combine(temp, "Plate.SLDPRT"), _ => false), "Parents escaped workspace");
            Check(WorkspacePolicy.RequireComment("  Added camera mount \n") == "Added camera mount", "Comment not trimmed");
            Denied(() => WorkspacePolicy.RequireComment("  "), "Blank comment accepted");
            Check(WorkspacePolicy.IsRepositoryName("2028-Robot") && WorkspacePolicy.IsRepositoryName("Library"), "Valid repository names refused");
            Check(!WorkspacePolicy.IsRepositoryName("../2028-Robot") && !WorkspacePolicy.IsRepositoryName("2028-Robot\\x") && !WorkspacePolicy.IsRepositoryName(""), "Unsafe repository name accepted");
            string library = Path.Combine(temp, "Library");
            string motor = Path.Combine(library, "Motors", "Kraken X60.SLDPRT");
            Check(WorkspacePolicy.LibraryCopyPath(library, root, motor) == Path.Combine(root, "90_COTS", "Motors", "Kraken X60.SLDPRT"), "Library copy location wrong");
            Denied(() => WorkspacePolicy.LibraryCopyPath(library, root, Path.Combine(root, "Plate.SLDPRT")), "Non-library file copied as library part");
            // Shape served by the Deck's joco.py.
            string json = "{\"version\": 1, \"active\": \"2028-Robot\", \"robots\": [" +
                "{\"name\": \"2027-Robot\", \"uuid\": \"b8f359f6-f382-4c21-998e-c00f2827aa38\", \"archived\": true}," +
                "{\"name\": \"2028-Robot\", \"uuid\": \"a4daad86-5e49-4ac4-9daf-de98891b87c4\", \"archived\": false}]," +
                "\"library\": {\"name\": \"Library\", \"uuid\": \"16dfa156-0828-4b34-a789-87ab9d9cff6b\"}}";
            var catalog = Catalog.Parse(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)));
            Check(catalog.Active == "2028-Robot" && catalog.Robots.Count == 2 && catalog.Robots[0].Archived && !catalog.Robots[1].Archived, "Catalog robots misread");
            Check(catalog.Library != null && catalog.Library.IsLibrary && catalog.Library.Id == new Guid("16dfa156-0828-4b34-a789-87ab9d9cff6b"), "Catalog library misread");
            Check(catalog.Robots[1].Repository.AbsoluteUri == "https://cad.imdad.stream/svn/2028-Robot/", "Repository URL wrong");
            Denied(() => Catalog.Parse(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{\"version\": 1, \"active\": \"x\", \"robots\": [{\"name\": \"../evil\", \"uuid\": \"b8f359f6-f382-4c21-998e-c00f2827aa38\"}]}"))), "Unsafe robot name accepted");
            Denied(() => Catalog.Parse(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("not json"))), "Garbage catalog accepted");
            string sha = new string('a', 64);
            Func<string, string, string, Catalog.AddinRelease> release = (v, f, h) => new Catalog.AddinRelease { Version = v, File = f, Sha256 = h };
            var v05 = new Version(0, 5, 0);
            Check(Updater.Offer(release("0.6.0", "JOCO-ROBOS-CAD-Setup-0.6.0.exe", sha), v05) != null, "Newer add-in not offered");
            Check(Updater.Offer(release("0.10.0", "JOCO-ROBOS-CAD-Setup-0.10.0.exe", sha), new Version(0, 9, 0)) != null, "Version compared as text");
            Check(Updater.Offer(release("0.5.0", "JOCO-ROBOS-CAD-Setup-0.5.0.exe", sha), v05) == null, "Same version offered");
            Check(Updater.Offer(release("0.4.0", "JOCO-ROBOS-CAD-Setup-0.4.0.exe", sha), v05) == null, "Downgrade offered");
            Check(Updater.Offer(release("0.6.0", "..\\evil.exe", sha), v05) == null, "Unsafe installer name offered");
            Check(Updater.Offer(release("0.6.0", "JOCO-ROBOS-CAD-Setup-0.7.0.exe", sha), v05) == null, "Mismatched installer version offered");
            Check(Updater.Offer(release("0.6.0", "JOCO-ROBOS-CAD-Setup-0.6.0.exe", "xyz"), v05) == null, "Missing checksum offered");
            Check(Updater.Offer(null, v05) == null, "Nothing published but offered");
            var withAddin = Catalog.Parse(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json.Substring(0, json.Length - 1) +
                ", \"addin\": {\"version\": \"0.6.0\", \"file\": \"JOCO-ROBOS-CAD-Setup-0.6.0.exe\", \"sha256\": \"" + sha + "\", \"required\": true}}")));
            Check(withAddin.Addin != null && withAddin.Addin.Required && withAddin.Addin.Version == "0.6.0", "Catalog add-in release misread");
            string tempRoot = Path.Combine(temp, "Temp");
            Check(WorkspacePolicy.IsTemporary(Path.Combine(tempRoot, "swx14316", "IC~~", "Screw3.step.SLDPRT"), tempRoot), "Imported temp part not recognized");
            Check(!WorkspacePolicy.IsTemporary(Path.Combine(temp, "Temporary", "Plate.SLDPRT"), tempRoot), "Sibling-prefix folder treated as temp");
            Check(!WorkspacePolicy.IsTemporary(part, tempRoot), "Robot part treated as temp");
            // Set Aside folders never collide, even twice in the same second.
            string aside = Path.Combine(temp, "Set Aside");
            var now = new DateTime(2026, 9, 30, 10, 43, 5);
            string first = WorkspacePolicy.UniqueFolder(aside, now);
            Directory.CreateDirectory(first);
            string second = WorkspacePolicy.UniqueFolder(aside, now);
            Check(first.EndsWith("2026-09-30 104305") && second.EndsWith("2026-09-30 104305 (2)"), "Set Aside folder collision");
            Check(WorkspacePolicy.TooLong(@"C:\" + new string('a', 250)) && !WorkspacePolicy.TooLong(@"C:\JOCO-ROBOS\2026-Robot\x.SLDPRT"), "Path length rule");
            // FRCDesignLib rules match FRCDesignApp: range conditions and per-option visibility.
            var size = new FrcChoice { Id = "Size", Kind = "enum", Default = "S", Options = new System.Collections.Generic.List<FrcOption> {
                new FrcOption { Id = "S" }, new FrcOption { Id = "M" }, new FrcOption { Id = "L" }, new FrcOption { Id = "XL" } } };
            var bore = new FrcChoice { Id = "Bore", Kind = "enum", Default = "Round", Options = new System.Collections.Generic.List<FrcOption> {
                new FrcOption { Id = "Round" }, new FrcOption { Id = "Hex" }, new FrcOption { Id = "Big" } },
                OptionRules = new System.Collections.Generic.List<FrcOptionRule> {
                    new FrcOptionRule { Options = new System.Collections.Generic.List<string> { "Big" }, VisibleWhen = new FrcCondition { Mode = "range", Id = "Size", Start = "L", End = "XL" } },
                    new FrcOptionRule { Options = new System.Collections.Generic.List<string> { "Hex" }, VisibleWhen = new FrcCondition { Mode = "equals", Id = "Size", Value = "M" } } } };
            var all = new System.Collections.Generic.List<FrcChoice> { size, bore };
            Func<string, string> visible = s => String.Join(",", bore.VisibleOptions(new System.Collections.Generic.Dictionary<string, string> { { "Size", s } }, all).Select(o => o.Id));
            Check(visible("S") == "Round" && visible("M") == "Round,Hex" && visible("XL") == "Round,Big", "Option visibility rules");
            Check(new FrcCondition { Mode = "any", Children = new System.Collections.Generic.List<FrcCondition>() }.Holds(new System.Collections.Generic.Dictionary<string, string>(), all), "Empty rule never hides");
            // Custom lengths from FRCDesignLib: checked in the panel, sent in the option's unit.
            var shaft = new FrcChoice { Id = "Length", Name = "Length", Kind = "number", Unit = "in", Min = "0", Max = "36", Default = "1" };
            string typed;
            Check(shaft.CheckNumber(" 2,5 ", out typed) == null && typed == "2.5", "Decimal comma length");
            Check(shaft.CheckNumber("40", out typed) == "Length must be between 0 and 36 in." && typed == null, "Length over the maximum");
            Check(shaft.CheckNumber("abc", out typed) != null && shaft.CheckNumber("NaN", out typed) != null, "Non-number length");
            Check(new FrcChoice { Name = "Teeth", Integer = true, Min = "0", Max = "255" }.CheckNumber("3.5", out typed) != null, "Fractional whole number");
            Check(WorkspacePolicy.SolidWorksYear("34.1.0") == 2026 && WorkspacePolicy.SolidWorksYear("x") == 0, "SOLIDWORKS year from revision number");
            Check(WorkspacePolicy.SolidWorksProblem(0, "2026") != null && WorkspacePolicy.SolidWorksProblem(0, null) == null, "Unknown SOLIDWORKS version fails safe");
            Check(WorkspacePolicy.SolidWorksProblem(2026, "2026") == null && WorkspacePolicy.SolidWorksProblem(2026, null) == null &&
                WorkspacePolicy.SolidWorksProblem(2027, "2026").Contains("not edit") && WorkspacePolicy.SolidWorksProblem(2025, "2026").Contains("Update SOLIDWORKS"),
                "Team SOLIDWORKS version rule");
            string report = Diagnostics.Sanitize("Authorization: Basic c2FyYWg6c2VjcmV0MTIz\npassword=hunter2hunter2 ok\ncode K7QM-3XRP-9TDW here\n" +
                "https://sarah:pw123@cad.imdad.stream/svn\nX-Joco-Token: abcdef0123456789\nr42 sarah: Added camera mount");
            Check(!report.Contains("c2FyYWg6") && !report.Contains("hunter2") && !report.Contains("K7QM") && !report.Contains("pw123") &&
                !report.Contains("abcdef0123456789") && report.Contains("Added camera mount") && report.Contains("cad.imdad.stream"), "Diagnostics keep no secrets: " + report);
            SubmitChecks(temp);
            PaneChecks();
            Console.WriteLine("PASS: " + assertions + " add-in checks");
        }
        finally { Directory.Delete(temp, true); }
    }

    // The panel shows one obvious next action for the situation, never a wall of commands.
    static void PaneChecks()
    {
        var season = new WorkspaceInfo("1997-Robot", Guid.NewGuid(), false, false);
        string part = Path.Combine(season.Root, "30_Shooter", "ShooterPlate.SLDPRT");
        string asm = Path.Combine(season.Root, "30_Shooter", "Shooter.SLDASM");
        Func<WorkspaceSnapshot> fresh = () => new WorkspaceSnapshot { Info = season, Local = 10, Head = 10 };
        var now = DateTime.Now;

        var s = PaneState.Describe(null, null, null, null, false, null, now);
        Check(s.ShowOpen && s.SubmitCount == 0 && s.EditTarget == null, "Not signed in: only Open Robot");
        var snap = fresh();
        s = PaneState.Describe("sam", snap, null, null, false, null, now);
        Check(s.ShowOpen && !s.ShowCloseAndUpdate && s.EditTarget == null && s.SubmitCount == 0, "Nothing open: Open Robot");
        s = PaneState.Describe("sam", snap, null, part, true, null, now, robotOpen: true);
        Check(!s.ShowOpen && s.EditTarget == "ShooterPlate" && s.ActiveStatus.Contains("Nobody else"), "Free read-only part: Edit it");
        s = PaneState.Describe("sam", snap, null, asm, true, null, now, robotOpen: true);
        Check(s.EditTarget == "", "Assembly: generic Edit (may lock the selected part)");
        snap.Locks[part] = "sarah";
        snap.LockedSince[part] = now.AddMinutes(-5);
        s = PaneState.Describe("sam", snap, null, part, true, null, now, robotOpen: true);
        Check(s.EditTarget == null && s.ActiveStatus.Contains("sarah is editing this since") && s.ActiveTone == Tone.Bad, "Teammate's file: who and since when, no button");
        snap = fresh(); snap.Locks[part] = "sam"; snap.Mine.Add(part); snap.Changed.Add(part);
        s = PaneState.Describe("sam", snap, null, part, false, null, now, robotOpen: true);
        Check(s.EditTarget == null && s.SubmitCount == 1 && s.ActiveStatus.Contains("editing this (saved)") && s.Locks.Contains("ShooterPlate"), "Editing: Submit 1");
        snap = fresh(); snap.Head = 12; snap.Incoming.Add("r11 sarah: intake"); snap.Incoming.Add("r12 sarah: arm");
        s = PaneState.Describe("sam", snap, null, part, true, null, now, robotOpen: true);
        Check(s.ShowCloseAndUpdate && !s.CanAutoUpdate && s.Sync.Contains("2 new changes"), "Teammate changes with robot open: Close & Update");
        s = PaneState.Describe("sam", snap, null, null, false, null, now);
        Check(s.CanAutoUpdate && !s.ShowCloseAndUpdate, "Nothing open: teammate changes come in by themselves");
        snap.PendingSubmit = true;
        s = PaneState.Describe("sam", snap, null, null, false, null, now);
        Check(s.InterruptedSubmit && s.Pending.Contains("interrupted"), "Interrupted Submit is shown with Submit to settle it");
        snap.PendingSubmit = false;
        snap.New.Add(Path.Combine(season.Root, "New.SLDPRT"));
        s = PaneState.Describe("sam", snap, null, null, false, null, now);
        Check(!s.CanAutoUpdate && !s.ShowCloseAndUpdate && s.Details.Contains("after you Submit") && s.SubmitCount == 1, "Own unsubmitted work: Submit first");
    }

    // The Submit window's preflight: every predictable problem, as structured issues with the right fix buttons.
    static void SubmitChecks(string temp)
    {
        string previous = Environment.CurrentDirectory;
        Environment.CurrentDirectory = temp; // Off Windows the base folder is relative; keep it inside the test folder.
        var season = new WorkspaceInfo("1999-Robot", Guid.NewGuid(), false, false);
        string root = season.Root;
        try
        {
            Func<string, string> file = relative =>
            {
                string path = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, "cad");
                return path;
            };
            string existing = file(Path.Combine("10_Drivetrain", "Frame", "24in2x1.SLDPRT"));
            string duplicate = file(Path.Combine("30_Shooter", "Bearings", "24in2x1.SLDPRT"));
            string shooter = file(Path.Combine("30_Shooter", "Shooter.SLDASM"));
            string camera = file(Path.Combine("30_Shooter", "CameraMount.SLDPRT"));
            string plate = file(Path.Combine("30_Shooter", "ShooterPlate.SLDPRT"));
            string outside = Path.Combine(temp, "Desktop", "REV_Gearbox.SLDASM");
            Directory.CreateDirectory(Path.GetDirectoryName(outside));
            File.WriteAllText(outside, "cad");
            string interconnect = Path.Combine(temp, "swtemp", "Imported.SLDPRT");

            var plan = new SubmitPlan();
            plan.Items.Add(new SubmitItem { Kind = SubmitKind.Modified, Path = shooter, Workspace = season });
            plan.Items.Add(new SubmitItem { Kind = SubmitKind.New, Path = camera, Workspace = season });
            plan.Items.Add(new SubmitItem { Kind = SubmitKind.ReleaseOnly, Path = plate, Workspace = season });
            var references = new System.Collections.Generic.Dictionary<string, string[]> { { shooter, new[] { camera, outside } } };
            var documents = new System.Collections.Generic.List<OpenDocument>();
            var locks = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var acknowledged = new System.Collections.Generic.HashSet<string>();
            Func<string[], System.Collections.Generic.List<SubmitIssue>> run = chosen => SubmitCheck.Run(new SubmitCheckInput
            {
                Plan = plan, Selected = new System.Collections.Generic.HashSet<string>(chosen, StringComparer.OrdinalIgnoreCase),
                Workspaces = new[] { season }, Documents = documents, LockedBy = locks, User = "sarah", Acknowledged = acknowledged,
                References = path => references.ContainsKey(path) ? references[path] : new string[0], TempFolder = Path.Combine(temp, "swtemp"),
            });

            var issues = run(new[] { shooter, plate });
            Check(issues.Count == 2 && issues.All(x => x.Blocking), "Expected outside reference + unchecked new file");
            var needs = issues.Single(x => x.Key == "needs:" + camera);
            Check(needs.Actions.SequenceEqual(new[] { IssueAction.IncludeFile }) && needs.Title == "Shooter.SLDASM needs CameraMount.SLDPRT",
                "Unchecked referenced file must offer Include, never Leave it out");
            var away = issues.Single(x => x.Key == "outside:" + shooter);
            Check(away.Actions.Contains(IssueAction.ImportIntoRobot) && away.Other == outside, "Outside reference must offer Import into robot");

            references[shooter] = new[] { camera };
            Check(run(new[] { shooter, camera, plate }).Count == 0, "Clean submission reported issues");
            Check(SubmitCheck.SubmitLabel(plan.Items) == "Submit 2" && SubmitCheck.SubmitLabel(plan.Items.Where(x => x.Kind == SubmitKind.ReleaseOnly)) == "Release 1",
                "Submit button count");

            plan.Items.Add(new SubmitItem { Kind = SubmitKind.New, Path = duplicate, Workspace = season });
            var same = run(new[] { duplicate }).Single();
            Check(same.Blocking && same.Other == existing && same.Actions.SequenceEqual(new[] { IssueAction.ShowFile, IssueAction.ShowOther }),
                "Duplicate name: show both files, no automatic rename");
            Check(run(new[] { shooter, camera }).Count == 0, "Unchecked duplicate still reported");

            // Only writable robot documents are saved; read-only team files are locked first; unrelated files are left alone.
            documents.Add(new OpenDocument { Path = shooter, Title = "Shooter", Dirty = true });
            documents.Add(new OpenDocument { Path = plate, Title = "ShooterPlate", Dirty = true, ReadOnly = true });
            documents.Add(new OpenDocument { Path = existing, Title = "24in2x1", Dirty = true, ReadOnly = true });
            documents.Add(new OpenDocument { Path = Path.Combine(root, "00_Master", "Robot.SLDASM"), Title = "Robot", Dirty = true, ReadOnly = true });
            documents.Add(new OpenDocument { Path = outside, Title = "REV_Gearbox", Dirty = true });
            locks[existing] = "imdad";
            issues = run(new[] { shooter, camera });
            var save = issues.Single(x => x.Key == "save");
            Check(save.Blocking && save.Files.SequenceEqual(new[] { shooter }) && save.Actions.SequenceEqual(new[] { IssueAction.SaveDocuments }),
                "Save must cover only writable robot documents");
            var readOnly = issues.Single(x => x.Key == "readonly:" + plate);
            Check(!readOnly.Blocking && readOnly.Actions.SequenceEqual(new[] { IssueAction.LockFile }), "Read-only unsaved part must offer Lock, not Save");
            Check(!issues.Single(x => x.Key == "locked:" + existing).Actions.Any(), "Someone else's file offered a fix");
            Check(issues.Count == 3, "Rebuilt read-only assembly or outside document reported");
            documents.Clear();

            // A reference to a CAD file that exists nowhere is reported, never skipped silently; "Submit anyway" acknowledges it.
            references[shooter] = new[] { camera, Path.Combine(temp, "Gone", "Lost Bracket.SLDPRT") };
            var lost = run(new[] { shooter, camera }).Single();
            Check(lost.Blocking && lost.Key.StartsWith("missing:" + shooter + ":") && lost.Title.Contains("isn't on this computer") && lost.Description.Contains("Lost Bracket"),
                "Unresolved reference reported");
            acknowledged.Add(lost.Key);
            Check(run(new[] { shooter, camera }).Count == 0, "Acknowledged missing reference");
            references[shooter] = new[] { camera, Path.Combine(temp, "Gone", "Other Bracket.SLDPRT") };
            Check(run(new[] { shooter, camera }).Single().Description.Contains("Other Bracket"), "Submit anyway doesn't cover a different missing file found later");
            references[shooter] = new[] { camera, Path.Combine(temp, "Gone", "table.xlsx") };
            Check(run(new[] { shooter, camera }).Count == 0, "Non-CAD dependency is a documented limit, not an issue");
            // Imported-only parts need an explicit "Submit anyway"; missing files and mentor problems never block the rest.
            references[shooter] = new[] { camera, interconnect };
            var temporary = run(new[] { shooter, camera }).Single();
            Check(temporary.Key.StartsWith(SubmitCheck.TemporaryKey) && temporary.Blocking && temporary.Actions.SequenceEqual(new[] { IssueAction.SubmitAnyway }), "Temporary reference");
            acknowledged.Add(temporary.Key);
            Check(run(new[] { shooter, camera }).Count == 0, "Acknowledged warning still blocks");
            plan.Restore.Add(new SubmitItem { Kind = SubmitKind.Modified, Path = Path.Combine(root, "Gone.SLDPRT"), Workspace = season, Reason = "Missing" });
            plan.Blocked.Add("Odd — needs mentor repair");
            issues = run(new[] { shooter, camera });
            Check(issues.Count == 2 && !issues.Any(x => x.Blocking) && issues.Any(x => x.Actions.Contains(IssueAction.RestoreFiles)), "Missing files must offer Restore without blocking");
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
