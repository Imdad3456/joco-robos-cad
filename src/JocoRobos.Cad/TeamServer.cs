using System;
using System.IO;
using System.Net;
using Microsoft.Win32;

namespace JocoRobos.Cad
{
    /// <summary>
    /// Which team server this computer uses. Any team can run its own server, so the address isn't built in: a student enters
    /// it once (their mentor gives it to them with their setup code), or school IT presets it for every Windows user
    /// (HKLM\Software\JOCO ROBOS\CAD, value Server). Computers that used Team 5919's server before this setting existed keep it.
    /// </summary>
    internal static class TeamServer
    {
        private const string Key = @"Software\JOCO ROBOS\CAD";
        // Team 5919's server, where every copy installed before the address became a setting points.
        internal const string Original = "https://cad.team5919.org/";
        private static Uri address;

        /// <summary>The server, or null when this computer hasn't been told yet.</summary>
        internal static Uri Address
        {
            get
            {
                if (address != null) return address;
                address = Read(Registry.CurrentUser) ?? Read(Registry.LocalMachine);
                if (address == null && UsedBeforeSetting())
                {
                    // Installed before the address became a setting: it has always been Team 5919's server. Remember that.
                    address = new Uri(Original);
                    try { Save(address); } catch (Exception) { } // Still used this session; saved next time.
                }
                return address;
            }
        }

        internal static bool IsSet { get { return Address != null; } }

        /// <summary>Earlier addresses of the same server: robot copies downloaded from them move over automatically.</summary>
        internal static string[] OldHosts
        {
            get
            {
                var current = Address;
                return current != null && current.Host == new Uri(Original).Host ? new[] { "cad.imdad.stream" } : new string[0];
            }
        }

        /// <summary>For tests: use this server without saving it.</summary>
        internal static void Use(Uri server) { address = server; }

        internal static void Save(Uri server)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(Key))
                key.SetValue("Server", server.AbsoluteUri);
            address = server;
        }

        private static Uri Read(RegistryKey hive)
        {
            try
            {
                using (var key = hive.OpenSubKey(Key))
                {
                    Uri server;
                    return WorkspacePolicy.TryServerAddress(key?.GetValue("Server") as string, out server) ? server : null;
                }
            }
            catch (Exception) { return null; }
        }

        // A saved sign-in or a downloaded robot means this computer was set up for Team 5919's server.
        private static bool UsedBeforeSetting()
        {
            try
            {
                if (CredentialStore.Read() != null) return true;
            }
            catch (Exception) { return true; } // Unreadable saved sign-in: it exists.
            foreach (string folder in new[] { @"C:\JOCO-ROBOS", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "JOCO-ROBOS") })
            {
                try { if (Directory.Exists(folder) && Directory.GetDirectories(folder, "*-Robot").Length > 0) return true; }
                catch (Exception) { }
            }
            return false;
        }

        /// <summary>
        /// Checks an address really is a CAD Hub server before saving it: its files need a sign-in under this software's
        /// name. Throws with a message for the student otherwise.
        /// </summary>
        internal static void Check(Uri server)
        {
            var request = (HttpWebRequest)WebRequest.Create(new Uri(server, "catalog.json"));
            request.Timeout = 15000;
            request.AllowAutoRedirect = false;
            request.UserAgent = "JOCO-ROBOS-CAD";
            try
            {
                using (var response = (HttpWebResponse)request.GetResponse())
                    throw new InvalidOperationException(server.Host + " answered, but it isn't a CAD Hub server. Check the address with your mentor.");
            }
            catch (WebException failure) when (failure.Response is HttpWebResponse response)
            {
                using (response)
                {
                    string challenge = response.Headers["WWW-Authenticate"] ?? "";
                    // The servers' sign-in name kept the project's original name, so every team's server (old or new) is recognized.
                    if (response.StatusCode == HttpStatusCode.Unauthorized && challenge.IndexOf("JOCO ROBOS CAD", StringComparison.OrdinalIgnoreCase) >= 0) return;
                    throw new InvalidOperationException(server.Host + " answered, but it isn't a CAD Hub server (HTTP " + (int)response.StatusCode +
                        "). Check the address with your mentor.");
                }
            }
            catch (WebException failure)
            {
                throw new InvalidOperationException("Couldn't reach " + server.Host + ". Check the address and your internet connection.\n\n" +
                    NetworkProblem.Describe(failure, server.Host), failure);
            }
        }
    }
}
