using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace JocoRobos.Cad
{
    /// <summary>One recovery copy: the file it was taken from and when.</summary>
    internal sealed class RecoveryCopy
    {
        internal string Copy;
        internal string Original;
        internal string Season;
        internal string Relative;
        internal DateTime TakenAt;
    }

    /// <summary>
    /// Automatic recovery copies of team files a student is editing and hasn't saved, so a SOLIDWORKS crash or a power cut costs
    /// minutes, not an afternoon. They live outside the robot (%LOCALAPPDATA%\JocoRobos.Cad\Recovery\Season\time\path in robot),
    /// so SVN, Submit, Update and Set Aside never see them, and they clean themselves up. Only plain files here, so it's tested directly.
    /// </summary>
    internal static class Recovery
    {
        internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
        // No mouse or keyboard input for this long first: a copy of a big assembly can pause SOLIDWORKS for a few seconds.
        internal static readonly TimeSpan Pause = TimeSpan.FromSeconds(20);
        internal static readonly TimeSpan KeepFor = TimeSpan.FromDays(14);
        internal const int KeepPerFile = 5;
        private const string Stamp = "yyyy-MM-dd HHmmss";
        private const string Marker = "running.txt";

        internal static string DefaultRoot
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JocoRobos.Cad", "Recovery"); }
        }

        /// <summary>The season folder (2027-Robot, Library) a robot file is in, and that folder; false for anything else.</summary>
        internal static bool Locate(string baseFolder, string file, out string season, out string root)
        {
            season = root = null;
            if (String.IsNullOrEmpty(baseFolder) || String.IsNullOrEmpty(file) || !WorkspacePolicy.IsSubmittableCad(file)) return false;
            string full, fullBase = Path.GetFullPath(baseFolder).TrimEnd(Path.DirectorySeparatorChar);
            try { full = Path.GetFullPath(file); }
            catch (Exception) { return false; }
            if (!full.StartsWith(fullBase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
            string[] parts = full.Substring(fullBase.Length + 1).Split(Path.DirectorySeparatorChar);
            if (parts.Length < 2 || !WorkspacePolicy.IsRepositoryName(parts[0]) || parts.Any(p => p.Equals(".svn", StringComparison.OrdinalIgnoreCase))) return false;
            season = parts[0];
            root = Path.Combine(fullBase, season);
            return true;
        }

        /// <summary>Where this round's copy of a file goes: the same name and folders as in the robot, so putting it back is a plain copy.</summary>
        internal static string CopyPath(string recoveryRoot, string season, string seasonRoot, string file, DateTime now)
        {
            string relative = Path.GetFullPath(file).Substring(Path.GetFullPath(seasonRoot).TrimEnd(Path.DirectorySeparatorChar).Length + 1);
            return Path.Combine(recoveryRoot, season, now.ToString(Stamp, CultureInfo.InvariantCulture), relative);
        }

        /// <summary>
        /// Whether a round of copies is due: the student did something since the last one, it's been Interval (longer when copies
        /// are slow: a round that took 30 s waits 10 minutes), and they've paused.
        /// </summary>
        internal static bool Due(DateTime now, DateTime lastRound, DateTime lastInput, TimeSpan lastDuration)
        {
            if (lastInput <= lastRound || now - lastInput < Pause) return false;
            var wait = TimeSpan.FromTicks(Math.Max(Interval.Ticks, lastDuration.Ticks * 20));
            return now - lastRound >= wait;
        }

        /// <summary>Every copy on this computer, newest first. Folders that aren't CAD Hub's are ignored.</summary>
        internal static List<RecoveryCopy> List(string recoveryRoot, string baseFolder)
        {
            var copies = new List<RecoveryCopy>();
            if (!Directory.Exists(recoveryRoot)) return copies;
            foreach (string seasonFolder in Directory.GetDirectories(recoveryRoot))
            {
                string season = Path.GetFileName(seasonFolder);
                if (!WorkspacePolicy.IsRepositoryName(season)) continue;
                foreach (string round in Directory.GetDirectories(seasonFolder))
                {
                    DateTime taken;
                    if (!DateTime.TryParseExact(Path.GetFileName(round), Stamp, CultureInfo.InvariantCulture, DateTimeStyles.None, out taken)) continue;
                    foreach (string file in Directory.GetFiles(round, "*", SearchOption.AllDirectories).Where(WorkspacePolicy.IsSubmittableCad))
                    {
                        string relative = file.Substring(round.Length + 1);
                        copies.Add(new RecoveryCopy
                        {
                            Copy = file, Season = season, Relative = relative, TakenAt = taken,
                            Original = Path.Combine(baseFolder, season, relative),
                        });
                    }
                }
            }
            return copies.OrderByDescending(c => c.TakenAt).ToList();
        }

        /// <summary>
        /// The newest copy of each file taken since a time, when it holds work the robot's file doesn't have (the file was never saved
        /// after it, or is gone). savedAt says when the robot's file was last saved; null if it doesn't exist.
        /// </summary>
        internal static List<RecoveryCopy> Unsaved(IEnumerable<RecoveryCopy> copies, DateTime since, Func<string, DateTime?> savedAt)
        {
            return copies.Where(c => c.TakenAt >= since)
                .GroupBy(c => c.Original, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(c => c.TakenAt).First())
                .Where(c => { var saved = savedAt(c.Original); return saved == null || saved.Value < c.TakenAt; })
                .OrderByDescending(c => c.TakenAt).ToList();
        }

        /// <summary>Deletes all but the newest KeepPerFile copies of each file, and any older than KeepFor. Returns how many were deleted.</summary>
        internal static int Prune(string recoveryRoot, string baseFolder, DateTime now)
        {
            int deleted = 0;
            foreach (var file in List(recoveryRoot, baseFolder).GroupBy(c => c.Season + "\\" + c.Relative, StringComparer.OrdinalIgnoreCase))
                foreach (var old in file.OrderByDescending(c => c.TakenAt).Where((c, i) => i >= KeepPerFile || now - c.TakenAt > KeepFor))
                {
                    try { File.SetAttributes(old.Copy, FileAttributes.Normal); File.Delete(old.Copy); deleted++; }
                    catch (IOException) { } // Open in SOLIDWORKS right now: next time.
                    catch (UnauthorizedAccessException) { }
                }
            if (Directory.Exists(recoveryRoot))
                foreach (string season in Directory.GetDirectories(recoveryRoot).Where(d => WorkspacePolicy.IsRepositoryName(Path.GetFileName(d))))
                    RemoveEmptyFolders(season);
            return deleted;
        }

        private static void RemoveEmptyFolders(string folder)
        {
            foreach (string child in Directory.GetDirectories(folder)) RemoveEmptyFolders(child);
            try { if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>
        /// Marks SOLIDWORKS (with CAD Hub) as running. Returns when the previous session started if it never closed normally (a crash,
        /// a power cut, ended from Task Manager), else null. isRunning tells whether that session's process is still alive (two SOLIDWORKS).
        /// </summary>
        internal static DateTime? StartSession(string recoveryRoot, DateTime now, int processId, Func<int, bool> isRunning)
        {
            string marker = Path.Combine(recoveryRoot, Marker);
            DateTime? crashed = null;
            try
            {
                if (File.Exists(marker))
                {
                    string[] fields = File.ReadAllText(marker).Trim().Split(' ');
                    int previous;
                    DateTime started;
                    if (fields.Length == 2 && Int32.TryParse(fields[0], out previous) &&
                        DateTime.TryParseExact(fields[1], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out started))
                    {
                        if (isRunning(previous)) return null; // Another SOLIDWORKS is using CAD Hub; leave its marker alone.
                        crashed = started;
                    }
                }
                Directory.CreateDirectory(recoveryRoot);
                File.WriteAllText(marker, processId + " " + now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return crashed;
        }

        /// <summary>SOLIDWORKS is closing normally (or CAD Hub is being turned off): the next start isn't after a crash.</summary>
        internal static void EndSession(string recoveryRoot, int processId)
        {
            string marker = Path.Combine(recoveryRoot, Marker);
            try
            {
                if (File.Exists(marker) && File.ReadAllText(marker).Trim().StartsWith(processId + " ", StringComparison.Ordinal)) File.Delete(marker);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
