using System;
using System.IO;
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
            Console.WriteLine("PASS: " + assertions + " workspace and lock-ownership checks");
        }
        finally { Directory.Delete(temp, true); }
    }
}
