using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace JocoRobos.Cad
{
    /// <summary>
    /// Automatic recovery copies (rules in Recovery.cs): while a student pauses, a copy of each team file they're editing with unsaved
    /// changes, so a crash loses minutes. Never changes the open documents, the robot folder, locks or anything Submit sees.
    /// </summary>
    public sealed partial class Addin
    {
        private Timer recoveryTimer;
        private DateTime lastRecoveryRound;
        private TimeSpan lastRecoveryDuration;

        [StructLayout(LayoutKind.Sequential)]
        private struct LastInputInfo { internal uint Size; internal uint Time; }

        [DllImport("user32.dll")]
        private static extern bool GetLastInputInfo(ref LastInputInfo info);

        // When the mouse or keyboard was last used on this computer.
        private static DateTime LastInput(DateTime now)
        {
            var info = new LastInputInfo { Size = (uint)Marshal.SizeOf(typeof(LastInputInfo)) };
            if (!GetLastInputInfo(ref info)) return now; // Unknown: treat as busy, so nothing pauses under the student's hand.
            uint idle = unchecked((uint)System.Environment.TickCount - info.Time);
            return now - TimeSpan.FromMilliseconds(idle);
        }

        private void StartRecovery()
        {
            lastRecoveryRound = DateTime.Now;
            DateTime? crashedSession = null;
            try
            {
                crashedSession = Recovery.StartSession(Recovery.DefaultRoot, DateTime.Now, Process.GetCurrentProcess().Id, IsSolidWorksProcess);
            }
            catch (Exception exception) { ErrorLog.Write("recovery: start", exception); }
            recoveryTimer = new Timer { Interval = 30 * 1000 };
            recoveryTimer.Tick += (s, e) =>
            {
                try { TakeRecoveryCopies(); }
                catch (Exception exception) { ErrorLog.Write("recovery copies", exception); }
            };
            recoveryTimer.Start();
            if (crashedSession != null)
            {
                // Once SOLIDWORKS has finished starting: a message box during start-up can hide behind its splash screen.
                var offer = new Timer { Interval = 5000 };
                offer.Tick += (s, e) =>
                {
                    offer.Stop();
                    offer.Dispose();
                    try { OfferRecoveredWork(crashedSession.Value); }
                    catch (Exception exception) { ErrorLog.Write("recovery: offer", exception); }
                };
                offer.Start();
            }
            else PruneRecovery();
        }

        private void StopRecovery()
        {
            recoveryTimer?.Stop();
            recoveryTimer?.Dispose();
            recoveryTimer = null;
            try { Recovery.EndSession(Recovery.DefaultRoot, Process.GetCurrentProcess().Id); }
            catch (Exception exception) { System.Diagnostics.Trace.WriteLine("JOCO recovery: " + exception); }
        }

        private static bool IsSolidWorksProcess(int id)
        {
            try { return Process.GetProcessById(id).ProcessName.Equals("SLDWORKS", StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { return false; } // Not running.
        }

        private static void PruneRecovery()
        {
            try { Recovery.Prune(Recovery.DefaultRoot, WorkspaceInfo.BaseFolder, DateTime.Now); }
            catch (Exception exception) { ErrorLog.Write("recovery: clean up", exception); }
            // Previews are throwaway copies: remove a day's old ones (one still open in SOLIDWORKS stays until next time).
            try
            {
                if (Directory.Exists(PreviewFolder))
                    foreach (string file in Directory.GetFiles(PreviewFolder).Where(f => File.GetCreationTime(f) < DateTime.Now.AddDays(-1)))
                        try { File.SetAttributes(file, FileAttributes.Normal); File.Delete(file); }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
            }
            catch (Exception exception) { ErrorLog.Write("recovery: clean up previews", exception); }
        }

        // Never in the middle of something: a CAD Hub command or side panel, a SOLIDWORKS command (a PropertyManager is open), or the Submit window.
        private bool RecoveryMustWait()
        {
            if (busy || application == null || ToolPage.IsOpen || (submitWindow != null && !submitWindow.IsDisposed && submitWindow.Visible)) return true;
            int command;
            string title;
            bool uiActive;
            application.GetRunningCommandInfo(out command, out title, out uiActive);
            return command > 0;
        }

        // A sketch being edited, or a part being edited inside an assembly: saving a copy then could end that edit.
        private static bool InTheMiddleOfAnEdit(ModelDoc2 doc)
        {
            if (doc.SketchManager.ActiveSketch != null) return true;
            if (doc.GetType() != (int)swDocumentTypes_e.swDocASSEMBLY) return false;
            var target = ((AssemblyDoc)doc).GetEditTarget() as ModelDoc2;
            return target != null && !String.Equals(target.GetPathName(), doc.GetPathName(), StringComparison.OrdinalIgnoreCase);
        }

        private void TakeRecoveryCopies()
        {
            var now = DateTime.Now;
            if (!Recovery.Due(now, lastRecoveryRound, LastInput(now), lastRecoveryDuration) || RecoveryMustWait()) return;
            lastRecoveryRound = now; // Even if something below fails: try again next interval, not every 30 seconds.
            var editing = OpenDocuments().Where(d =>
            {
                string path = d.GetPathName(), season, root;
                return !String.IsNullOrEmpty(path) && Recovery.Locate(WorkspaceInfo.BaseFolder, path, out season, out root) &&
                    !d.IsOpenedReadOnly() && d.GetSaveFlag();
            }).ToList();
            if (editing.Count == 0) return;
            try
            {
                string drive = Path.GetPathRoot(Recovery.DefaultRoot);
                if (new DriveInfo(drive).AvailableFreeSpace < HealthCheck.LowDisk) { ErrorLog.Step("recovery copies skipped: low disk space"); return; }
            }
            catch (Exception) { } // Can't tell: try anyway.
            var clock = Stopwatch.StartNew();
            int copied = 0;
            foreach (var doc in editing)
            {
                string path = doc.GetPathName(), season, root;
                try
                {
                    if (InTheMiddleOfAnEdit(doc) || !Recovery.Locate(WorkspaceInfo.BaseFolder, path, out season, out root)) continue;
                    string copy = Recovery.CopyPath(Recovery.DefaultRoot, season, root, path, now);
                    if (WorkspacePolicy.TooLong(copy)) { ErrorLog.Step("recovery copy skipped (path too long): " + path); continue; }
                    int error = SaveCopy(doc, copy);
                    if (error == 0) copied++;
                    else ErrorLog.Step("recovery copy of " + path + " failed: SOLIDWORKS error " + error);
                }
                catch (Exception exception) { ErrorLog.Write("recovery copy of " + path, exception); }
            }
            lastRecoveryDuration = clock.Elapsed;
            ErrorLog.Slow("recovery copies of " + copied + " file(s)", clock.ElapsedMilliseconds, 5000);
            if (copied > 0) PruneRecovery();
        }

        // Files from the last session that crashed whose copies hold work the robot doesn't, until the student reviews them.
        private List<RecoveryCopy> recoveredWork;

        private static string PreviewFolder { get { return Path.Combine(Recovery.DefaultRoot, "Preview"); } }

        // After SOLIDWORKS closed unexpectedly: the panel's sync line says so, with Review. No window pops up over the student's work.
        private void OfferRecoveredWork(DateTime crashedSession)
        {
            var lost = Recovery.Unsaved(Recovery.List(Recovery.DefaultRoot, WorkspaceInfo.BaseFolder), crashedSession, SavedAt);
            if (lost.Count == 0) { PruneRecovery(); return; }
            recoveredWork = lost;
            ErrorLog.Step("recovered work after a crash: " + String.Join(", ", lost.Select(c => Path.GetFileName(c.Original))));
            RenderStatus();
        }

        private static DateTime? SavedAt(string path) { return File.Exists(path) ? File.GetLastWriteTime(path) : (DateTime?)null; }

        // Tools → CAD Hub → Recovery Copies: the same review, for any copy that holds work its robot file doesn't.
        public void RecoveryCopies() { ReviewRecovery(); }

        private void ReviewRecovery()
        {
            Execute(() =>
            {
                bool afterCrash = recoveredWork != null;
                var copies = recoveredWork ?? Recovery.Unsaved(Recovery.List(Recovery.DefaultRoot, WorkspaceInfo.BaseFolder), DateTime.MinValue, SavedAt);
                if (copies.Count == 0)
                {
                    recoveredWork = null;
                    Message("No recovered work: every recovery copy is older than its robot file.\n\nWhile you edit a robot file and haven't saved it, CAD Hub " +
                        "keeps a copy every few minutes (when you pause), in case SOLIDWORKS closes unexpectedly. Copies are kept for " + Recovery.KeepFor.Days +
                        " days; Diagnostics says how many there are.");
                    return;
                }
                string explanation = afterCrash
                    ? "SOLIDWORKS closed unexpectedly. These copies have work your robot files don't."
                    : "These copies have work your robot files don't (newer than the files' last save).";
                using (var dialog = new RecoveryDialog(explanation, copies, RecoveryStatus, PreviewRecovery, RestoreRecovery, SaveRecoveryCopy,
                    () => Process.Start("explorer.exe", "/select,\"" + copies[0].Copy + "\"")))
                    dialog.ShowDialog(new SolidWorksWindow());
                recoveredWork = null; // Reviewed: the copies stay in the folder, the panel stops asking.
            });
        }

        // What a row says before anything is clicked, from the last status check (Restore checks the server again).
        private string RecoveryStatus(RecoveryCopy copy)
        {
            var snapshot = new[] { robotSnapshot, librarySnapshot }.FirstOrDefault(x => x != null && x.Info.Name == copy.Season);
            if (snapshot == null) return "Not in your current robot: Save a copy to keep it";
            string owner;
            if (snapshot.Mine.Contains(copy.Original) || snapshot.New.Contains(copy.Original)) return "✓ You're still editing it: Restore puts this copy back";
            if (snapshot.Locks.TryGetValue(copy.Original, out owner) && owner != paneUser) return owner + " is editing it now: Save a copy to keep yours";
            return "You're not editing it any more: Save a copy, then Edit it and redo your changes";
        }

        // Opens the copy read-only, renamed and outside the robot, so it can't be mistaken for (or saved over) the robot file.
        private void PreviewRecovery(RecoveryCopy copy)
        {
            try
            {
                if (!File.Exists(copy.Copy)) { Message("That recovery copy is gone.", MessageBoxIcon.Warning); return; }
                bool assembly = copy.Original.EndsWith(".sldasm", StringComparison.OrdinalIgnoreCase);
                if (assembly && MessageBox.Show(new SolidWorksWindow(), "The preview of " + Path.GetFileNameWithoutExtension(copy.Original) + " uses the robot's " +
                    "current parts, which may be newer than when the copy was made. Positions, mates and assembly features are the copy's.\n\nOpen the preview?",
                    Title, MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
                string preview = Path.Combine(PreviewFolder, RecoveryRestore.RecoveredName(copy.Original, copy.TakenAt));
                int errors = 0, warnings = 0;
                var open = FindOpen(preview);
                if (open != null)
                {
                    application.ActivateDoc3(open.GetTitle(), false, (int)swRebuildOnActivation_e.swDontRebuildActiveDoc, ref errors);
                    return;
                }
                Directory.CreateDirectory(PreviewFolder);
                if (File.Exists(preview)) File.SetAttributes(preview, FileAttributes.Normal);
                File.Copy(copy.Copy, preview, true);
                File.SetAttributes(preview, FileAttributes.ReadOnly);
                int type = assembly ? (int)swDocumentTypes_e.swDocASSEMBLY :
                    preview.EndsWith(".slddrw", StringComparison.OrdinalIgnoreCase) ? (int)swDocumentTypes_e.swDocDRAWING : (int)swDocumentTypes_e.swDocPART;
                if (application.OpenDoc6(preview, type, (int)(swOpenDocOptions_e.swOpenDocOptions_Silent | swOpenDocOptions_e.swOpenDocOptions_ReadOnly), "", ref errors, ref warnings) == null)
                    Message("SOLIDWORKS couldn't open the preview (error " + errors + "). Save a copy instead and open it from there.", MessageBoxIcon.Warning);
            }
            catch (Exception exception)
            {
                ErrorLog.Write("recovery: preview", exception);
                Message("Couldn't open the preview: " + exception.Message, MessageBoxIcon.Warning);
            }
        }

        // Keeps the copy outside the robot (the Desktop by default), under a name that says what it is.
        private void SaveRecoveryCopy(RecoveryCopy copy)
        {
            try
            {
                using (var dialog = new SaveFileDialog { Title = "Save the recovery copy", FileName = RecoveryRestore.RecoveredName(copy.Original, copy.TakenAt),
                    InitialDirectory = System.Environment.GetFolderPath(System.Environment.SpecialFolder.DesktopDirectory), OverwritePrompt = true,
                    Filter = "SOLIDWORKS file|*" + Path.GetExtension(copy.Original) })
                {
                    if (dialog.ShowDialog(new SolidWorksWindow()) != DialogResult.OK) return;
                    string target = Path.GetFullPath(dialog.FileName);
                    if (target.StartsWith(WorkspaceInfo.BaseFolder + "\\", StringComparison.OrdinalIgnoreCase))
                    {
                        Message("Save it outside " + WorkspaceInfo.BaseFolder + " (for example on the Desktop), so it doesn't become part of the robot.", MessageBoxIcon.Warning);
                        return;
                    }
                    File.Copy(copy.Copy, target, true);
                    File.SetAttributes(target, FileAttributes.Normal);
                    ShowFlash("✓ Saved " + Path.GetFileName(target));
                }
            }
            catch (Exception exception)
            {
                ErrorLog.Write("recovery: save a copy", exception);
                Message("Couldn't save the copy: " + exception.Message, MessageBoxIcon.Warning);
            }
        }

        // Puts a copy back into the robot, only when RecoveryRestore says it's safe (checked with the server just now) and the student
        // confirms. The robot file is kept in Set Aside first, and replaced in one step, so a failure leaves it as it was.
        private bool RestoreRecovery(RecoveryCopy copy)
        {
            try
            {
                var login = GetLogin(false);
                if (login == null) return false;
                var catalog = LoadCatalog(login);
                var workspace = new[] { catalog.Robot, catalog.Library }.FirstOrDefault(w => w != null && !w.Archived && w.Name == copy.Season);
                var facts = new RestoreFacts { Name = Path.GetFileName(copy.Original), CopyExists = File.Exists(copy.Copy), InCurrentRobot = workspace != null,
                    OpenInSolidWorks = FindOpen(copy.Original) != null, SavedAt = SavedAt(copy.Original), CopyTakenAt = copy.TakenAt,
                    ReadOnlyOnDisk = File.Exists(copy.Original) && (File.GetAttributes(copy.Original) & FileAttributes.ReadOnly) != 0 };
                if (workspace != null && facts.CopyExists && !facts.OpenInSolidWorks)
                {
                    var fresh = OperationDialog.Run("Checking with the team server…", () => new SvnWorkspace(login, workspace).Snapshot());
                    string owner, newer;
                    // A new file isn't locked by anyone: it exists only on this computer until it's submitted.
                    facts.LockedByMe = fresh.Mine.Contains(copy.Original) || fresh.New.Contains(copy.Original);
                    facts.LockedBy = fresh.Locks.TryGetValue(copy.Original, out owner) && owner != login.UserName ? owner : null;
                    facts.NewerFrom = fresh.IncomingFiles.TryGetValue(copy.Original, out newer) ? newer : null;
                }
                string why = RecoveryRestore.WhyNot(facts);
                if (why != null) { Message("Not restored.\n\n" + why, MessageBoxIcon.Warning); return false; }
                string name = Path.GetFileNameWithoutExtension(copy.Original);
                string setAside = Path.Combine(WorkspacePolicy.UniqueFolder(Path.Combine(WorkspaceInfo.BaseFolder, "Set Aside"), DateTime.Now), workspace.Name,
                    copy.Original.Substring(workspace.Root.Length + 1));
                if (MessageBox.Show(new SolidWorksWindow(), "Replace " + name + " in the robot with the recovery copy from " + copy.TakenAt.ToString("h:mm tt") + "?\n\n" +
                    (File.Exists(copy.Original) ? "The current " + name + " is kept in\n" + Path.GetDirectoryName(setAside) + "\nso you can go back to it.\n\n" : "") +
                    "Only this file changes. Open it to check, then Save and Submit as usual.",
                    Title, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return false;
                if (FindOpen(copy.Original) != null) { Message("Not restored: " + name + " was opened meanwhile. Close it and try again.", MessageBoxIcon.Warning); return false; }
                ErrorLog.Step("recovery: restoring " + copy.Original + " from " + copy.Copy);
                string staged = copy.Original + ".cadhub-restore";
                File.Copy(copy.Copy, staged, true);
                File.SetAttributes(staged, FileAttributes.Normal);
                if (File.Exists(copy.Original))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(setAside));
                    File.Copy(copy.Original, setAside, false);
                    if (new FileInfo(setAside).Length != new FileInfo(copy.Original).Length)
                    {
                        File.Delete(staged);
                        throw new IOException("the copy kept in Set Aside doesn't match the robot file");
                    }
                    File.Replace(staged, copy.Original, null);
                }
                else File.Move(staged, copy.Original);
                ShowFlash("✓ Restored " + name + " from the " + copy.TakenAt.ToString("h:mm tt") + " copy." + (File.Exists(setAside) ? " The previous version is in Set Aside." : ""));
                return true;
            }
            catch (Exception exception)
            {
                ErrorLog.Write("recovery: restore", exception);
                Message("Not restored: " + exception.Message + "\n\nYour robot file is as it was.", MessageBoxIcon.Warning);
                return false;
            }
        }

        private static string RecoverySummary()
        {
            var copies = Recovery.List(Recovery.DefaultRoot, WorkspaceInfo.BaseFolder);
            return copies.Count == 0 ? "none" : copies.Count + ", newest " + copies[0].TakenAt.ToString("yyyy-MM-dd HH:mm") + " (" + Path.GetFileName(copies[0].Original) + ")";
        }
    }
}
