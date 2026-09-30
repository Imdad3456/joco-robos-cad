using System;
using System.IO;

namespace JocoRobos.Cad
{
    /// <summary>
    /// %LOCALAPPDATA%\JocoRobos.Cad\errors.log: what went wrong and where, so a problem a student reports
    /// ("SOLIDWORKS just closed") can be found. Never throws.
    /// </summary>
    internal static class ErrorLog
    {
        internal static string FilePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JocoRobos.Cad", "errors.log"); }
        }

        internal static void Write(string where, Exception exception)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                // Keep it small: start over past 1 MB.
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > 1024 * 1024) File.Delete(FilePath);
                File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + Updater.Current + "  " + where + "\r\n" + exception + "\r\n\r\n");
            }
            catch (Exception) { } // Logging must never cause a second problem.
            System.Diagnostics.Trace.WriteLine("JOCO " + where + ": " + exception);
        }
    }
}
