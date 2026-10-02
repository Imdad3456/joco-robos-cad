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
        TeamServer.Use(new Uri(TeamServer.Original)); // The fixtures below are Team 5919's server.
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
            Check(catalog.Robots[1].Repository.AbsoluteUri == "https://cad.team5919.org/svn/2028-Robot/", "Repository URL wrong");
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
            ServerChecks();
            TeamServer.Use(new Uri(TeamServer.Original));
            Check(WorkspacePolicy.IsOldAddress(new Uri("https://cad.imdad.stream/svn/2026-Robot/"), new Uri("https://cad.team5919.org/svn/2026-Robot/"), WorkspaceInfo.OldServerHosts) &&
                !WorkspacePolicy.IsOldAddress(new Uri("https://cad.imdad.stream/svn/2026-Robot/"), new Uri("https://cad.team5919.org/svn/Library/"), WorkspaceInfo.OldServerHosts) &&
                !WorkspacePolicy.IsOldAddress(new Uri("https://evil.example/svn/2026-Robot/"), new Uri("https://cad.team5919.org/svn/2026-Robot/"), WorkspaceInfo.OldServerHosts),
                "Robot copies move only from this server's old address, same repository");
            SubmitChecks(temp);
            PaneChecks();
            RobotFileChecks(temp);
            PartsChecks(temp);
            WhereUsedChecks();
            ToolChecks();
            FeatureChecks();
            HealthChecks();
            Console.WriteLine("PASS: " + assertions + " add-in checks");
        }

        finally { Directory.Delete(temp, true); }
    }

    // Any team's server: what a student types becomes https://host/, and nothing else gets through.
    static void ServerChecks()
    {
        Uri server;
        foreach (var typed in new[] { "cad.example.org", " CAD.Example.org ", "https://cad.example.org", "https://cad.example.org/", "cad.example.org/" })
            Check(WorkspacePolicy.TryServerAddress(typed, out server) && server.AbsoluteUri == "https://cad.example.org/", "Server address accepted: '" + typed + "'");
        Check(WorkspacePolicy.TryServerAddress("cad.example.org:8443", out server) && server.AbsoluteUri == "https://cad.example.org:8443/", "Server with a port");
        foreach (var typed in new[] { "", "   ", "http://cad.example.org", "ftp://cad.example.org", "https://cad.example.org/svn/2026-Robot",
            "https://user:pw@cad.example.org", "https://cad.example.org/?x=1", "localhost", "192.168.1.20", "cad example.org", "https://cad.example.org/#a" })
            Check(!WorkspacePolicy.TryServerAddress(typed, out server), "Server address refused: '" + typed + "'");
        // Only Team 5919's server has an old address to move robot copies from; any other team's has none.
        TeamServer.Use(new Uri("https://cad.example.org/"));
        Check(WorkspaceInfo.OldServerHosts.Length == 0 && new WorkspaceInfo("2026-Robot", Guid.NewGuid(), false, false).Repository.AbsoluteUri ==
            "https://cad.example.org/svn/2026-Robot/", "Another team's server: its own address, nothing moved from 5919's old one");
        TeamServer.Use(new Uri(TeamServer.Original));
        Check(WorkspaceInfo.OldServerHosts.Length == 1, "Team 5919 keeps moving copies from its old address");
    }

    // Where used: every assembly above a file, nearest first; what an assembly uses; loops can't hang it.
    static void WhereUsedChecks()
    {
        string R(string name) { return Path.Combine(Path.GetTempPath(), "wu", name); }
        var graph = new ReferenceGraph(new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>
        {
            [R("Robot.SLDASM")] = new System.Collections.Generic.List<string> { R("Shooter.SLDASM"), R("Drive.SLDASM") },
            [R("Shooter.SLDASM")] = new System.Collections.Generic.List<string> { R("Plate.SLDPRT"), R("Flywheel.SLDASM") },
            [R("Flywheel.SLDASM")] = new System.Collections.Generic.List<string> { R("Plate.SLDPRT"), R("Wheel.SLDPRT") },
            [R("Drive.SLDASM")] = new System.Collections.Generic.List<string> { R("Wheel.SLDPRT") },
            [R("LoopA.SLDASM")] = new System.Collections.Generic.List<string> { R("LoopB.SLDASM") },
            [R("LoopB.SLDASM")] = new System.Collections.Generic.List<string> { R("LoopA.SLDASM") },
        });
        var plate = graph.UsedBy(R("Plate.SLDPRT"));
        Check(plate.Count == 3 && plate[0].Item2 == 1 && plate.Count(x => x.Item2 == 1) == 2 && plate.Any(x => x.Item1 == R("Robot.SLDASM") && x.Item2 == 2),
            "Where used: direct users first, then up to the robot, each once");
        Check(graph.UsedBy(R("Robot.SLDASM")).Count == 0 && graph.Uses(R("Robot.SLDASM")).Count == 2, "The robot: used by nothing, uses its subsystems");
        Check(graph.UsedBy(R("LoopA.SLDASM")).Count == 1, "Circular references don't hang or repeat");
    }

    // Hole layout, belt and chain calculator, gears, and plate lightening: the math behind the SOLIDWORKS tools.
    static void ToolChecks()
    {
        Check(StockParts.FillRows(2, 0.5, 0.196).SequenceEqual(new[] { -0.5, 0, 0.5 }) && StockParts.FillRows(1, 0.5, 0.196).SequenceEqual(new[] { 0.0 }) &&
            StockParts.FillRows(1.5, 0.5, 0.196).SequenceEqual(new[] { -0.25, 0.25 }), "Fill the side: 3 rows on 2\", 2 on 1.5\", 1 on 1\" (0.5\" grid)");
        Check(StockParts.ParseInches("23.75") == 23.75 && StockParts.ParseInches("23 3/4\"") == 23.75 && StockParts.ParseInches("3/4") == 0.75 &&
            StockParts.ParseInches("abc") == null && StockParts.ParseInches("") == null, "Lengths typed as decimals, fractions, mixed numbers");
        var holes = StockParts.HolePositions(2.0, 0.25, 0.5, 0.196);
        Check(holes.SequenceEqual(new[] { 0.25, 0.75, 1.25, 1.75 }), "Holes every 0.5\" from 0.25\": " + String.Join(",", holes));
        Check(StockParts.HolePositions(3.0, 0.5, 1.0, 0.25).SequenceEqual(new[] { 0.5, 1.5, 2.5 }), "Other spacings and starts");
        Check(StockParts.RowOffsets(1, 0.5).SequenceEqual(new[] { 0.0 }) && StockParts.RowOffsets(2, 0.5).SequenceEqual(new[] { -0.25, 0.25 }) &&
            StockParts.RowOffsets(3, 0.5).SequenceEqual(new[] { -0.5, 0.0, 0.5 }), "Rows centered across the face");
        var htd = BeltChain.Kinds[0];
        double c = BeltChain.CenterDistance(htd, 30, 30, 100);
        Check(Math.Abs(c - (500 - 150) / 2.0 / 25.4) < 1e-6, "Equal pulleys: center = (belt − half the wrap) / 2 → " + c);
        double c2 = BeltChain.CenterDistance(htd, 18, 36, 120);
        Check(Math.Abs(BeltChain.Length(c2, BeltChain.PitchDiameter(htd, 18), BeltChain.PitchDiameter(htd, 36)) - 120 * htd.Pitch) < 1e-6, "Unequal pulleys: the belt fits exactly");
        Check(double.IsNaN(BeltChain.CenterDistance(htd, 60, 60, 61)), "A belt too short to go around is refused");
        var chain = BeltChain.Kinds.First(k => k.Name == "#25 chain");
        var near = BeltChain.NearestLengths(chain, 16, 32, 6.0);
        Check(near.Item1 % 2 == 0 && near.Item2 == near.Item1 + 2 && BeltChain.CenterDistance(chain, 16, 32, near.Item1) <= 6.0 &&
            BeltChain.CenterDistance(chain, 16, 32, near.Item2) >= 6.0, "Chain: the even link counts either side of the wanted center");

        var gear = SpurGear.Outline(36, 20, 20, 6);
        double pitch = 36 / 20.0 / 2, outside = 38 / 20.0 / 2, root = pitch - 1.25 / 20;
        var radii = gear.Select(p => Math.Sqrt(p[0] * p[0] + p[1] * p[1])).ToList();
        Check(radii.Max() <= outside + 1e-9 && radii.Max() > outside - 1e-6 && radii.Min() >= root - 1e-9, "Gear: teeth reach the outside diameter, roots at the dedendum");
        Check(PlateLighten.SignedArea(gear) > 0 && Math.Abs(PlateLighten.SignedArea(gear) - Math.PI * pitch * pitch) < 0.1, "Gear outline counterclockwise, about the pitch circle's area");
        Check(SpurGear.Problem(36, 20, 20) == null && SpurGear.Problem(4, 20, 20) != null, "Gear sizes checked");

        // A 6×4 plate with four corner holes and one in the middle: pockets between, clear of the edge and every hole.
        var outline = new System.Collections.Generic.List<double[]> { new[] { 0.0, 0 }, new[] { 6.0, 0 }, new[] { 6.0, 4 }, new[] { 0.0, 4 } };
        var plateHoles = new System.Collections.Generic.List<Circle2> { new Circle2 { X = 0.5, Y = 0.5, R = 0.1 }, new Circle2 { X = 5.5, Y = 0.5, R = 0.1 },
            new Circle2 { X = 5.5, Y = 3.5, R = 0.1 }, new Circle2 { X = 0.5, Y = 3.5, R = 0.1 }, new Circle2 { X = 3, Y = 2, R = 0.25 } };
        var settings = new LightenSettings();
        var lighten = PlateLighten.Plan(outline, plateHoles, null, settings);
        Check(lighten.Pockets.Count >= 4 && lighten.Percent > 20 && lighten.Percent < 80, "Plate: several pockets, a sensible share removed (" + lighten.Pockets.Count + ", " + lighten.Percent.ToString("0") + "%)");
        var points = lighten.Pockets.SelectMany(p => p.Segments.SelectMany(seg => new[] { new[] { seg.X1, seg.Y1 }, new[] { seg.X2, seg.Y2 } })).ToList();
        Check(points.All(p => p[0] >= settings.Border - 1e-4 && p[0] <= 6 - settings.Border + 1e-4 && p[1] >= settings.Border - 1e-4 && p[1] <= 4 - settings.Border + 1e-4),
            "Pockets stay a border width from the edge");
        Check(points.All(p => plateHoles.All(h => Math.Sqrt((p[0] - h.X) * (p[0] - h.X) + (p[1] - h.Y) * (p[1] - h.Y)) >= h.R + settings.Ring - 1e-3)), "Pockets keep a ring around every hole");
        // Round holes and curved edges: the pockets' edges near them are arcs around the hole's center, not straight cuts.
        var round = new System.Collections.Generic.List<double[]>();
        for (int i = 0; i < 180; i++) round.Add(new[] { 4 * Math.Cos(2 * Math.PI * i / 180), 4 * Math.Sin(2 * Math.PI * i / 180) });
        var bearing = new System.Collections.Generic.List<Circle2> { new Circle2 { X = 0, Y = 0, R = 0.75 } };
        var disc = PlateLighten.Plan(round, bearing, null, settings);
        var arcs = disc.Pockets.SelectMany(p => p.Segments).Where(seg => seg.Arc).ToList();
        Check(disc.Count >= 4 && arcs.Any(seg => Math.Abs(seg.Cx) < 0.01 && Math.Abs(seg.Cy) < 0.01 && Math.Abs(Math.Sqrt(seg.X1 * seg.X1 + seg.Y1 * seg.Y1) - 0.9) < 0.01) &&
            arcs.Any(seg => Math.Abs(Math.Sqrt(seg.X1 * seg.X1 + seg.Y1 * seg.Y1) - 3.75) < 0.002 && Math.Abs(Math.Sqrt(seg.X2 * seg.X2 + seg.Y2 * seg.Y2) - 3.75) < 0.002 &&
                Math.Sqrt((seg.X2 - seg.X1) * (seg.X2 - seg.X1) + (seg.Y2 - seg.Y1) * (seg.Y2 - seg.Y1)) > 0.5),
            "Round plate with a bearing: pockets follow the ring around the bearing and the curved edge as arcs (" + disc.Count + " pockets)");
        Check(disc.Pockets.All(p => p.Segments.Count < 30), "Pocket outlines are a few lines and arcs, not hundreds of tiny pieces");
        var fitted = PlateLighten.Fit(round, 0.001);
        Check(fitted.Count <= 3 && fitted.All(seg => seg.Arc && !seg.Clockwise), "A sampled circle fits back to arcs, counterclockwise");
        Check(lighten.Pockets.All(p => p.Segments.Where(seg => seg.Arc).All(seg => Math.Abs(Math.Sqrt((seg.X1 - seg.Cx) * (seg.X1 - seg.Cx) + (seg.Y1 - seg.Cy) * (seg.Y1 - seg.Cy)) -
            Math.Sqrt((seg.X2 - seg.Cx) * (seg.X2 - seg.Cx) + (seg.Y2 - seg.Cy) * (seg.Y2 - seg.Cy))) < 1e-9)), "Every corner is a true arc");
        Check(PlateLighten.Plan(outline, plateHoles, null, new LightenSettings { Rib = 3 }).Pockets.Count == 0, "Ribs too wide for the plate: nothing cut");
        Check(PlateLighten.Triangulate(new System.Collections.Generic.List<double[]> { new[] { 0.0, 0 }, new[] { 1.0, 0 }, new[] { 0.0, 1 }, new[] { 1.0, 1 } }).Count == 2, "Four points: two triangles");
    }

    // Phase 1 framework: stored feature settings, team standards from the catalog, bearing hole sizes.
    static void FeatureChecks()
    {
        var p = new FeatureParams();
        p["kind"] = "gear";
        p.Set("teeth", 36);
        p["name"] = "a;b=c%d";
        var back = FeatureParams.Decode(p.Encode());
        Check(back["kind"] == "gear" && back.Number("teeth", 0) == 36 && back["name"] == "a;b=c%d" && back["missing"] == null && back.Number("missing", 7) == 7,
            "Feature settings survive storing, even with ; = % in them");
        string json = "{\"version\": 1, \"active\": \"2028-Robot\", \"robots\": [{\"name\": \"2028-Robot\", \"uuid\": \"a4daad86-5e49-4ac4-9daf-de98891b87c4\"}]," +
            "\"standards\": {\"fits\": {\"easy\": 0.004, \"normal\": 0.002, \"press\": 0.0005}, \"bearings\": [{\"name\": \"Swerve bearing\", \"od\": 1.375}, {\"name\": \"\", \"od\": 1}]}}";
        var catalog = Catalog.Parse(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)));
        Check(catalog.Standards.Normal == 0.002 && catalog.Standards.Easy == 0.004 && catalog.Standards.Bearings.Count == 1 &&
            catalog.Standards.Bearings[0].Name == "Swerve bearing", "Team standards read from the catalog; blank bearings dropped");
        var old = Catalog.Parse(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{\"version\": 1, \"active\": \"x\", \"robots\": []}")));
        Check(old.Standards.Normal == 0.0015 && old.Standards.Bearings.Count == 0, "A server without standards: defaults");
        var presets = BearingHoles.Presets(catalog.Standards);
        Check(presets[0].Team && presets[0].Name.Contains("Swerve") && presets.Count > 5, "Team bearings listed first");
        Check(BearingHoles.Bore(1.125, "Normal fit", TeamStandards.Defaults) == 1.1265 && BearingHoles.Bore(1.125, "Press fit", TeamStandards.Defaults) == 1.1255 &&
            BearingHoles.Bore(1.125, "Easy fit", TeamStandards.Defaults) == 1.1285 && BearingHoles.Bore(1.125, "Normal fit", catalog.Standards) == 1.127,
            "Bearing bores: outside diameter plus the team's clearance for the fit");
    }

    static void HealthChecks()
    {
        var healthy = HealthCheck.Evaluate(new HealthFacts { RobotDownloaded = true });
        Check(healthy.All(f => !f.Problem), "Nothing wrong: Healthy");
        var sick = HealthCheck.Evaluate(new HealthFacts { RobotDownloaded = true, UpdateAvailable = "9.0.0 (required)", FreeBytes = 100 * 1024 * 1024,
            WorkspaceProblems = { "30_Shooter/Plate.SLDPRT: deleted on this computer" }, MissingReferences = { @"C:\Users\x\Part1.SLDPRT" } });
        Check(sick.Count(f => f.Problem) == 4 && sick.TakeWhile(f => f.Problem).Count() == 4 && sick.Any(f => f.Text.Contains("Part1.SLDPRT")) &&
            sick.Any(f => f.Text.Contains("100 MB")), "Problems listed first, each with what to do");
        Check(HealthCheck.Evaluate(new HealthFacts { ServerProblem = "unreachable" }).Count(f => f.Problem) == 2, "Not downloaded: robot checks skipped");
        Timings.Record("Test op", 1500);
        Timings.Record("Test op", 500);
        Check(Timings.Report().Contains("Test op: last 0.5 s, slowest 1.5 s, average 1.0 s (2×)"), "Timings report " + Timings.Report());
    }

    // Parts list: buy (by vendor, part number) vs make, quantities, configurations, bought assemblies counted once.
    static void PartsChecks(string temp)
    {
        string root = Path.Combine(temp, "Parts", "2026-Robot");
        Func<string, string, PartUse> use = (relative, config) => new PartUse { Path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)), Configuration = config };
        var uses = new System.Collections.Generic.List<PartUse>();
        for (int i = 0; i < 3; i++) uses.Add(use("90_COTS/AndyMark/am-1635 500Hex Thin Collar Clamp.SLDPRT", "Default"));
        uses.Add(use("90_COTS/Hardware/Fasteners/97145A104_Retaining Ring.SLDPRT", null));
        uses.Add(use("90_COTS/REV/Motors/NEOmotor.SLDPRT", null));
        uses.Add(use("30_Shooter/Plate.SLDPRT", "Default"));
        uses.Add(use("30_Shooter/Plate.SLDPRT", "Default"));
        uses.Add(use("30_Shooter/Bracket^Shooter.SLDPRT", null));
        uses.Add(use("10_Drivetrain/Tube.SLDPRT", "Default"));
        uses.Add(use("10_Drivetrain/Tube.SLDPRT", "24in"));
        uses.Add(use("40_Climber/am-4668 2 Stage Climber.SLDASM", null));
        var rows = PartsSheet.Build(root, uses);
        Func<string, string, PartsRow> row = (name, config) => rows.Single(r => r.Name == name && r.Configuration == config);
        var collar = row("am-1635 500Hex Thin Collar Clamp", "");
        Check(collar.Buy && collar.Vendor == "AndyMark" && collar.PartNumber == "am-1635" && collar.Quantity == 3, "COTS part: vendor folder, part number, quantity");
        var ring = row("97145A104_Retaining Ring", "");
        Check(ring.Buy && ring.Vendor == "McMaster-Carr" && ring.PartNumber == "97145A104", "Hardware folder: the part number names the vendor");
        Check(row("NEOmotor", "").Vendor == "REV" && row("NEOmotor", "").PartNumber == "", "Vendor from the folder when there's no part number");
        Check(!row("Plate", "").Buy && row("Plate", "").Quantity == 2 && row("Plate", "").Vendor == "Team-made", "Team part: make, counted");
        Check(!row("Bracket", "").Buy && row("Bracket", "").Folder.Contains("inside its assembly"), "Virtual component: team-made, saved in its assembly");
        Check(row("Tube", "").Quantity == 1 && row("Tube", "24in").Quantity == 1, "Configurations are separate lines");
        Check(row("am-4668 2 Stage Climber", "").Buy && row("am-4668 2 Stage Climber", "").Vendor == "AndyMark", "Vendor-numbered assembly bought as one");
        Check(rows.TakeWhile(r => r.Buy).Count() == rows.Count(r => r.Buy), "Buy lines first");
        Check(PartsSheet.IsBoughtAssembly(root, Path.Combine(root, "90_COTS", "AndyMark", "Gearbox.SLDASM")) &&
            !PartsSheet.IsBoughtAssembly(root, Path.Combine(root, "30_Shooter", "Shooter.SLDASM")), "COTS assemblies counted once, team assemblies opened up");
        // Duplicates: the same item in two files, but not numbered pieces of one item.
        var dupUses = new System.Collections.Generic.List<PartUse> { use("90_COTS/AndyMark/am-1635 Collar.SLDPRT", null), use("90_COTS/AndyMark/am-1635 Collar (1).SLDPRT", null),
            use("30_Shooter/am-1635 Collar.SLDPRT", null), use("90_COTS/WCP/WCP-0940_1.SLDPRT", null), use("90_COTS/WCP/WCP-0940_2.SLDPRT", null),
            use("90_COTS/REV/Copy of NEO.SLDPRT", null), use("90_COTS/REV/NEO.SLDPRT", null) };
        var dups = PartsSheet.Duplicates(PartsSheet.Build(root, dupUses));
        Check(dups.Count == 2 && dups.Any(d => d.Count == 3 && d.All(r => r.Name.StartsWith("am-1635"))) && dups.Any(d => d.Any(r => r.Name == "Copy of NEO")) &&
            !dups.Any(d => d.Any(r => r.Name.StartsWith("WCP-0940"))), "Duplicates: copies and same name in two folders, not numbered pieces");
        Check(PartsSheet.Build(root, uses).Single(r => r.Name == "Plate").Uses.Count == 2, "Each line keeps its component instances (to select them)");
        string csv = PartsSheet.Csv(new[] { new PartsRow { Name = "=HYPERLINK(1)", Vendor = "A, B", Quantity = 1 } });
        Check(csv.Contains("'=HYPERLINK(1)") && csv.Contains("\"A, B\""), "CSV: no formulas, commas quoted");
    }

    // The Robot tab's file browser: only robot CAD, real folders, searchable by name or folder.
    static void RobotFileChecks(string temp)
    {
        string robot = Path.Combine(temp, "Browse", "2096-Robot");
        Action<string> make = relative =>
        {
            string path = Path.Combine(robot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, "x");
        };
        foreach (string f in new[] { Path.Combine("00_Master", "Robot.SLDASM"), Path.Combine("30_Shooter", "Shooter.SLDASM"),
            Path.Combine("30_Shooter", "Structure", "ShooterPlate.SLDPRT"), Path.Combine("30_Shooter", "Structure", "Plate.SLDDRW"),
            Path.Combine(".svn", "pristine", "Hidden.SLDPRT"), Path.Combine("30_Shooter", "~$Shooter.SLDASM"),
            Path.Combine("30_Shooter", "notes.txt"), Path.Combine("70_Docs", "readme.txt"), Path.Combine("90_COTS", "Motors", "Kraken X60.SLDPRT") })
            make(f);
        var index = RobotFileIndex.Build(robot);
        Check(index.Files.Count == 5 && !index.Files.Any(f => f.Contains(".svn") || f.Contains("~$") || f.EndsWith(".txt")), "Only robot CAD is listed: " + String.Join(", ", index.Files));
        Check(index.SubfoldersOf("").SequenceEqual(new[] { "00_Master", "30_Shooter", "90_COTS" }), "Folders without CAD (and .svn) are left out");
        Check(index.FilesIn("30_Shooter").SequenceEqual(new[] { "Shooter.SLDASM" }) && index.SubfoldersOf("30_Shooter").SequenceEqual(new[] { "Structure" }), "Folder contents");
        var shooter = index.Search("shooter");
        Check(shooter.Count == 3 && Path.GetFileName(shooter[0]) == "Shooter.SLDASM" && Path.GetFileName(shooter[1]) == "ShooterPlate.SLDPRT" && shooter[2].EndsWith("Plate.SLDDRW"),
            "Search by name first, then by folder: " + String.Join(", ", shooter));
        Check(index.Search("STRUCTURE plate").Count == 2 && index.Search("kraken").Single().EndsWith("Kraken X60.SLDPRT") && index.Search("zzz").Count == 0, "Case-insensitive, all words, folder names");
        Check(WorkspacePolicy.IsRobotFile(robot, Path.Combine(robot, "30_Shooter", "Shooter.SLDASM")) &&
            !WorkspacePolicy.IsRobotFile(robot, Path.Combine(robot, "30_Shooter", "~$Shooter.SLDASM")) &&
            !WorkspacePolicy.IsRobotFile(robot, Path.Combine(robot, "30_Shooter", "notes.txt")) &&
            !WorkspacePolicy.IsRobotFile(robot, Path.Combine(temp, "Browse", "2095-Robot", "Other.SLDPRT")) &&
            !WorkspacePolicy.IsRobotFile(robot, Path.Combine(robot, "..", "2095-Robot", "Sneaky.SLDPRT")) &&
            !WorkspacePolicy.IsRobotFile(robot, Path.Combine(robot, ".svn", "pristine", "Hidden.SLDPRT")), "Only this robot's CAD can be opened");
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
        Check(s.AskOwner == "sarah", "Teammate's file: offer to ask sarah for it");
        // Requests to this student come from the server; only well-formed ones are shown.
        var asked = EditRequests.Parse(new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{\"requests\": [" +
            "{\"id\": \"a1\", \"season\": \"2026-Robot\", \"path\": \"30_Shooter/Plate.SLDPRT\", \"from\": \"sarah\"}," +
            "{\"id\": \"a2\", \"season\": \"../x\", \"path\": \"Plate.SLDPRT\", \"from\": \"eve\"}," +
            "{\"id\": \"a3\", \"season\": \"2026-Robot\", \"path\": \"../../Windows/x.SLDPRT\", \"from\": \"eve\"}]}")));
        Check(asked.Count == 1 && asked[0].From == "sarah" && asked[0].Path == "30_Shooter/Plate.SLDPRT", "Edit requests parsed, bad ones dropped");
        snap = fresh(); snap.Locks[part] = "sam"; snap.Mine.Add(part); snap.Changed.Add(part);
        s = PaneState.Describe("sam", snap, null, part, false, null, now, robotOpen: true);
        Check(s.EditTarget == null && s.SubmitCount == 1 && s.ActiveStatus.Contains("editing this (saved)") && s.Locks.Contains("ShooterPlate"), "Editing: Submit 1");
        snap = fresh(); snap.Head = 12; snap.Incoming.Add("r11 sarah: intake"); snap.Incoming.Add("r12 sarah: arm");
        s = PaneState.Describe("sam", snap, null, part, true, null, now, robotOpen: true);
        Check(s.ShowCloseAndUpdate && !s.CanAutoUpdate && s.Sync.Contains("2 new changes"), "Teammate changes with robot open: Close & Update");
        s = PaneState.Describe("sam", snap, null, null, false, null, now);
        Check(s.CanAutoUpdate && !s.ShowCloseAndUpdate, "Nothing open: teammate changes come in by themselves");
        // A new Library part (mentor upload) with nothing new in the robot still comes down.
        var librarySeason = new WorkspaceInfo("Library", Guid.NewGuid(), false, true);
        var lib = new WorkspaceSnapshot { Info = librarySeason, Local = 5, Head = 6 };
        lib.Incoming.Add("#6 mentor1: Add bracket");
        var robotNow = fresh();
        s = PaneState.Describe("sam", robotNow, lib, null, false, null, now);
        Check(s.CanAutoUpdate && s.Sync.Contains("1 new change") && s.Details.Contains("Library #6"), "Library-only news is fetched too");
        s = PaneState.Describe("sam", robotNow, lib, part, true, null, now, robotOpen: true);
        Check(s.ShowCloseAndUpdate, "Library-only news with the robot open: Close & Update");
        lib.New.Add(Path.Combine(librarySeason.Root, "Mine.SLDPRT"));
        s = PaneState.Describe("sam", robotNow, lib, null, false, null, now);
        Check(!s.CanAutoUpdate && s.Details.Contains("the Library's after you Submit"), "Library news waits for the Library's own unsubmitted work");
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
            var teamReferences = new System.Collections.Generic.Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            var documents = new System.Collections.Generic.List<OpenDocument>();
            var locks = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var acknowledged = new System.Collections.Generic.HashSet<string>();
            Func<string[], System.Collections.Generic.List<SubmitIssue>> run = chosen => SubmitCheck.Run(new SubmitCheckInput
            {
                Plan = plan, Selected = new System.Collections.Generic.HashSet<string>(chosen, StringComparer.OrdinalIgnoreCase),
                Workspaces = new[] { season }, Documents = documents, LockedBy = locks, User = "sarah", Acknowledged = acknowledged,
                References = path => references.ContainsKey(path) ? references[path] : new string[0],
                TeamReferences = path => teamReferences.ContainsKey(path) ? teamReferences[path] : new string[0], TempFolder = Path.Combine(temp, "swtemp"),
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
            // Problems already in the team's version of a changed file aren't the student's doing: they never block.
            string inherited = Path.Combine(temp, "Elsewhere", "Part1.SLDPRT"), imported = Path.Combine(Path.GetDirectoryName(interconnect), "PDH.step.SLDPRT");
            references[shooter] = new[] { camera, inherited, imported };
            teamReferences[shooter] = new[] { inherited, imported };
            Check(run(new[] { shooter, camera }).Count == 0, "Missing/temporary references inherited from the team's version blocked Submit");
            references[shooter] = new[] { camera, inherited, imported, Path.Combine(temp, "Gone", "My New Bracket.SLDPRT") };
            var added = run(new[] { shooter, camera }).Single();
            Check(added.Description.Contains("My New Bracket") && !added.Description.Contains("Part1"), "Only the newly added missing reference is reported");
            teamReferences.Clear();
            // Rebuild problems: a warning about the file being submitted, never blocking.
            documents.Add(new OpenDocument { Path = shooter, Title = "Shooter.SLDASM", RebuildProblems = 3 });
            var rebuild = run(new[] { shooter, camera }).Where(x => x.Key.StartsWith("rebuild:")).ToList();
            Check(rebuild.Count == 1 && !rebuild[0].Blocking && rebuild[0].Title.Contains("3 rebuild problems"), "Rebuild problems shown, not blocking");
            Check(!run(new[] { camera }).Any(x => x.Key.StartsWith("rebuild:")), "Rebuild problems only for files being submitted");
            documents.Clear();
            // Virtual components live inside their assembly (SOLIDWORKS unpacks them to temp while it's open): never an issue.
            references[shooter] = new[] { camera, Path.Combine(Path.GetDirectoryName(interconnect), "Belt1-4^Shooter.SLDPRT"),
                Path.Combine(temp, "Gone", "Part6^Shooter.SLDPRT") };
            Check(run(new[] { shooter, camera }).Count == 0, "Virtual components flagged as temporary or missing");
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
