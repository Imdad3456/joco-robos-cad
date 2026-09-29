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
            Console.WriteLine("PASS: " + assertions + " workspace and lock-ownership checks");
        }
        finally { Directory.Delete(temp, true); }
    }
}
