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

        // After SOLIDWORKS closed unexpectedly: say which files have work newer than what's saved, and where it is.
        private void OfferRecoveredWork(DateTime crashedSession)
        {
            var lost = Recovery.Unsaved(Recovery.List(Recovery.DefaultRoot, WorkspaceInfo.BaseFolder), crashedSession,
                p => File.Exists(p) ? File.GetLastWriteTime(p) : (DateTime?)null);
            if (lost.Count == 0) { PruneRecovery(); return; }
            string list = String.Join("\n", lost.Take(8).Select(c => "  â€¢ " + Path.GetFileName(c.Original) + "  (copy from " + c.TakenAt.ToString("h:mm tt") + ")")) +
                (lost.Count > 8 ? "\n  â€¢ â€¦" : "");
            if (MessageBox.Show(new SolidWorksWindow(),
                "SOLIDWORKS closed unexpectedly last time. CAD Hub kept recovery copies of work you hadn't saved:\n\n" + list +
                "\n\nTo use one: open it from the folder and compare, or close that file in SOLIDWORKS and copy the recovery file over it " +
                "(only for files you're still editing). Your edit locks are still yours.\n\nOpen the folder now?",
                Title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                Process.Start("explorer.exe", "/select,\"" + lost[0].Copy + "\"");
        }

        public void RecoveryCopies()
        {
            Execute(() =>
            {
                var copies = Recovery.List(Recovery.DefaultRoot, WorkspaceInfo.BaseFolder);
                if (copies.Count == 0)
                {
                    Message("No recovery copies on this computer yet.\n\nWhile you edit a robot file and haven't saved it, CAD Hub keeps a copy every few minutes " +
                        "(when you pause), in case SOLIDWORKS closes unexpectedly. Copies are kept for " + Recovery.KeepFor.Days + " days.");
                    return;
                }
                Process.Start("explorer.exe", "/select,\"" + copies[0].Copy + "\"");
            });
        }

        private static string RecoverySummary()
        {
            var copies = Recovery.List(Recovery.DefaultRoot, WorkspaceInfo.BaseFolder);
            return copies.Count == 0 ? "none" : copies.Count + ", newest " + copies[0].TakenAt.ToString("yyyy-MM-dd HH:mm") + " (" + Path.GetFileName(copies[0].Original) + ")";
        }
    }
}
