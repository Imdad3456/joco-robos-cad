using System;
using System.Collections.Generic;
using System.Linq;

namespace JocoRobos.Cad
{
    /// <summary>What the add-in found on this computer, gathered by "Check This Computer".</summary>
    internal sealed class HealthFacts
    {
        internal string ServerProblem;          // null: reachable and signed in
        internal string UpdateAvailable;        // "1.6.1", "1.6.1 (required)", or null
        internal string SolidWorksProblem;      // from WorkspacePolicy.SolidWorksProblem
        internal bool RobotDownloaded;
        internal List<string> WorkspaceProblems = new List<string>();   // conflicts, missing files, lost locks…
        internal List<string> MissingReferences = new List<string>();   // files the robot assembly needs that aren't on this computer
        internal bool InterruptedSubmit;
        internal int UnsubmittedChanges;
        internal int IncomingChanges;
        internal long FreeBytes = long.MaxValue;
    }

    internal sealed class HealthFinding
    {
        internal bool Problem;
        internal string Text;
    }

    /// <summary>"Check This Computer": everything that can quietly go wrong, said only when it has. Pure, so it's tested directly.</summary>
    internal static class HealthCheck
    {
        internal const long LowDisk = 2L * 1024 * 1024 * 1024;

        internal static List<HealthFinding> Evaluate(HealthFacts facts)
        {
            var findings = new List<HealthFinding>();
            Action<bool, string, string> add = (ok, good, bad) => findings.Add(new HealthFinding { Problem = !ok, Text = ok ? good : bad });
            add(facts.ServerProblem == null, "Team server: reachable, and your sign-in works", "Team server: " + facts.ServerProblem);
            add(facts.UpdateAvailable == null, "Add-in: up to date (" + Updater.Current + ")",
                "Add-in: CAD Hub " + facts.UpdateAvailable + " is available. Click Install update in the panel (it installs when SOLIDWORKS closes).");
            add(facts.SolidWorksProblem == null, "SOLIDWORKS: the team's version", "SOLIDWORKS: " + facts.SolidWorksProblem);
            add(facts.RobotDownloaded, "Robot: downloaded", "Robot: not downloaded yet. Click Open Robot.");
            if (facts.RobotDownloaded)
            {
                add(facts.WorkspaceProblems.Count == 0, "Robot folder: no conflicts, missing files, or lost edits",
                    "Robot folder needs attention:\n  • " + String.Join("\n  • ", facts.WorkspaceProblems.Take(8)) + (facts.WorkspaceProblems.Count > 8 ? "\n  • …" : "") +
                    "\n  Tools → CAD Hub → Restore Deleted Files brings back missing ones; for the rest, Copy Diagnostics for a mentor.");
                add(facts.MissingReferences.Count == 0, "Robot assembly: every file it uses is on this computer",
                    "Robot assembly uses " + facts.MissingReferences.Count + " file(s) that aren't on this computer:\n  • " +
                    String.Join("\n  • ", facts.MissingReferences.Take(6).Select(System.IO.Path.GetFileName)) + (facts.MissingReferences.Count > 6 ? "\n  • …" : "") +
                    "\n  Teammates see them missing too. Open the robot and fix the references (or remove those components), then Submit.");
                add(!facts.InterruptedSubmit, "Submit: nothing interrupted", "Submit: an earlier Submit was interrupted. Click Submit; it finishes safely.");
            }
            add(facts.FreeBytes >= LowDisk, "Disk: enough free space", "Disk: only " + (facts.FreeBytes / (1024 * 1024)) + " MB free on the robot's drive. Free some space before updating.");
            return findings.OrderBy(f => f.Problem ? 0 : 1).ToList();
        }
    }
}
