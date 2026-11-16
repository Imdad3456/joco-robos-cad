using System;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace JocoRobos.Cad
{
    /// <summary>
    /// Signing in with Discord: the browser does Discord's part; this add-in then collects its CAD username and key from the
    /// team server with a secret (the verifier) that never goes through the browser. The key works as the SVN password and is
    /// the same on every computer the student signs in on. See server/discord_auth.py.
    /// </summary>
    internal sealed class DiscordSignIn
    {
        internal enum State { Pending, Done, Failed, Expired }

        private readonly string verifier;
        internal string StartUrl { get; }
        internal State Result { get; private set; }
        internal NetworkCredential Login { get; private set; }
        internal string Problem { get; private set; }

        internal DiscordSignIn(Uri server)
        {
            var bytes = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            verifier = Base64Url(bytes);
            string challenge;
            using (var sha = SHA256.Create()) challenge = Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
            StartUrl = new Uri(server, "account/discord/start?kind=addin&challenge=" + challenge).AbsoluteUri;
        }

        /// <summary>Asks the server once whether Discord finished. Network trouble counts as still waiting.</summary>
        internal State Check()
        {
            if (Result != State.Pending) return Result;
            string answer;
            try { answer = Accounts.Post("account/discord/poll", null, "{\"verifier\": " + Accounts.Json(verifier) + "}"); }
            catch (InvalidOperationException) { return Result; }
            Apply(answer);
            return Result;
        }

        /// <summary>The server's answer: {"status": "pending" | "done" | "error" | "unknown", ...}.</summary>
        internal void Apply(string json)
        {
            string status = Field(json, "status");
            if (status == "done")
            {
                string user = Field(json, "username"), key = Field(json, "key");
                if (String.IsNullOrEmpty(user) || String.IsNullOrEmpty(key)) { Fail("The server's answer wasn't readable. Try again."); return; }
                Login = new NetworkCredential(user, key);
                Result = State.Done;
            }
            else if (status == "error") Fail(Field(json, "message") ?? "Discord sign-in didn't work. Try again.");
            else if (status == "unknown") { Result = State.Expired; Problem = "That sign-in expired. Click Sign in with Discord again."; }
        }

        private void Fail(string problem)
        {
            Result = State.Failed;
            Problem = problem;
        }

        internal static string Base64Url(byte[] bytes)
        {
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        /// <summary>A string field of a flat JSON object (the server's answers are simple and its own).</summary>
        internal static string Field(string json, string name)
        {
            var match = Regex.Match(json ?? "", "\"" + Regex.Escape(name) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!match.Success) return null;
            return Regex.Replace(match.Groups[1].Value, @"\\(u[0-9a-fA-F]{4}|.)", m =>
            {
                string e = m.Groups[1].Value;
                if (e.Length == 5) return ((char)Convert.ToInt32(e.Substring(1), 16)).ToString();
                switch (e[0]) { case 'n': return "\n"; case 't': return "\t"; case 'r': return "\r"; case 'b': return "\b"; case 'f': return "\f"; default: return e; }
            });
        }
    }
}
