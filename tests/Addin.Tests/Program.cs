// Add-in unit checks that run without SOLIDWORKS or Windows: path safety, lock ownership, file naming,
// the season catalog, update offers, and library copy rules. Run: dotnet run --project tests/Addin.Tests
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
            Console.WriteLine("PASS: " + assertions + " add-in checks");
        }
        finally { Directory.Delete(temp, true); }
    }
}
