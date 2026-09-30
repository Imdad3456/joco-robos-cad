using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace JocoRobos.Cad
{
    /// <summary>Downloads a mentor-published installer and hands off to it once SOLIDWORKS closes.</summary>
    internal static class Updater
    {
        internal static Version Current
        {
            get
            {
                var attribute = (AssemblyFileVersionAttribute)Attribute.GetCustomAttribute(typeof(Updater).Assembly, typeof(AssemblyFileVersionAttribute));
                Version version;
                return attribute != null && Version.TryParse(attribute.Version, out version) ? Normalize(version) : new Version(0, 0, 0);
            }
        }

        /// <summary>True when SOLIDWORKS loaded a developer build (Register-Dev.ps1) instead of the installed copy.</summary>
        internal static bool IsDevelopmentBuild
        {
            get
            {
                string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "JOCO ROBOS CAD") + Path.DirectorySeparatorChar;
                return !typeof(Updater).Assembly.Location.StartsWith(installed, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static Version Normalize(Version version)
        {
            return new Version(version.Major, version.Minor, Math.Max(0, version.Build));
        }

        /// <summary>The offered release when it is safe to download and newer than this add-in; otherwise null.</summary>
        internal static Catalog.AddinRelease Offer(Catalog.AddinRelease release, Version current)
        {
            Version offered;
            if (release == null || release.File == null || release.Sha256 == null || !Version.TryParse(release.Version, out offered)) return null;
            if (!Regex.IsMatch(release.File, @"^JOCO-ROBOS-CAD-Setup-\d{1,4}\.\d{1,4}\.\d{1,4}\.exe$") ||
                release.File != "JOCO-ROBOS-CAD-Setup-" + release.Version + ".exe" ||
                !Regex.IsMatch(release.Sha256, "^[0-9a-f]{64}$")) return null;
            return Normalize(offered) > Normalize(current) ? release : null;
        }

        /// <summary>Downloads to the user's local app data and verifies the SHA-256 the server published.</summary>
        internal static string Download(NetworkCredential login, Catalog.AddinRelease release)
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JocoRobos.Cad", "updates");
            Directory.CreateDirectory(folder);
            string target = Path.Combine(folder, release.File);
            if (File.Exists(target) && Hash(target) == release.Sha256) return target;
            string partial = target + ".partial";
            var request = (HttpWebRequest)WebRequest.Create(new Uri(WorkspaceInfo.Server, "updates/" + Uri.EscapeDataString(release.File)));
            request.Timeout = 30000;
            request.ReadWriteTimeout = 120000;
            request.AllowAutoRedirect = false;
            request.UserAgent = "JOCO-ROBOS-CAD";
            request.Headers[HttpRequestHeader.Authorization] = "Basic " +
                Convert.ToBase64String(Encoding.UTF8.GetBytes(login.UserName + ":" + login.Password));
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var input = response.GetResponseStream())
            using (var output = File.Create(partial))
                input.CopyTo(output);
            if (Hash(partial) != release.Sha256)
            {
                File.Delete(partial);
                throw new InvalidOperationException("The downloaded update was damaged or does not match what mentors published. Nothing was installed; try again later.");
            }
            if (File.Exists(target)) File.Delete(target);
            File.Move(partial, target);
            return target;
        }

        private static string Hash(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>Starts the installer (Windows asks for admin approval). It waits for SOLIDWORKS to exit, installs, then reopens it.</summary>
        internal static void Launch(string installer)
        {
            string solidWorks = Process.GetCurrentProcess().MainModule.FileName;
            // Silent installs show no errors, so always keep a log next to the download.
            string log = Path.ChangeExtension(installer, ".log");
            Process.Start(new ProcessStartInfo
            {
                FileName = installer,
                Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /WAITFORSW \"/LOG=" + log + "\" \"/RELAUNCH=" + solidWorks + "\"",
                UseShellExecute = true,
            });
        }
    }
}
