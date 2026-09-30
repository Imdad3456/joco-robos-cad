using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace JocoRobos.Cad
{
    [DataContract]
    internal sealed class FrcOption
    {
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "name")] public string Name { get; set; }
        public override string ToString() { return Name; }
    }

    [DataContract]
    internal sealed class FrcCondition
    {
        [DataMember(Name = "mode")] public string Mode { get; set; }
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "value")] public string Value { get; set; }
        [DataMember(Name = "children")] public List<FrcCondition> Children { get; set; }
        [DataMember(Name = "start")] public string Start { get; set; }
        [DataMember(Name = "end")] public string End { get; set; }

        /// <summary>Same rules as FRCDesignApp (and the JOCO server): all / any / equals / range over an enum's option order.</summary>
        internal bool Holds(IDictionary<string, string> chosen, IList<FrcChoice> choices)
        {
            string current;
            if (Mode == "equals") return chosen.TryGetValue(Id ?? "", out current) && current == Value;
            if (Mode == "range")
            {
                var target = choices.FirstOrDefault(c => c.Id == Id && c.Kind == "enum");
                if (target == null) return true;
                var ids = (target.Options ?? new List<FrcOption>()).Select(o => o.Id).ToList();
                int start = ids.IndexOf(Start), end = ids.IndexOf(End);
                if (start < 0 || end < start) return true;
                return chosen.TryGetValue(Id ?? "", out current) && ids.GetRange(start, end - start + 1).Contains(current);
            }
            var results = (Children ?? new List<FrcCondition>()).Select(c => c.Holds(chosen, choices)).ToList();
            if (results.Count == 0) return true;
            return Mode == "any" ? results.Any(x => x) : results.All(x => x);
        }
    }

    [DataContract]
    internal sealed class FrcOptionRule
    {
        [DataMember(Name = "options")] public List<string> Options { get; set; }
        [DataMember(Name = "visibleWhen")] public FrcCondition VisibleWhen { get; set; }
    }

    [DataContract]
    internal sealed class FrcChoice
    {
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "kind")] public string Kind { get; set; }
        [DataMember(Name = "default")] public string Default { get; set; }
        [DataMember(Name = "options")] public List<FrcOption> Options { get; set; }
        [DataMember(Name = "visibleWhen")] public FrcCondition VisibleWhen { get; set; }
        [DataMember(Name = "optionRules")] public List<FrcOptionRule> OptionRules { get; set; }
        // Number options (custom lengths): typed in Unit, between Min and Max; the server converts for Onshape.
        [DataMember(Name = "unit")] public string Unit { get; set; }
        [DataMember(Name = "min")] public string Min { get; set; }
        [DataMember(Name = "max")] public string Max { get; set; }
        [DataMember(Name = "integer")] public bool Integer { get; set; }

        /// <summary>For a number option: null and the value to send, or a message saying what's wrong. The server checks again.</summary>
        internal string CheckNumber(string text, out string value)
        {
            value = null;
            double number, low, high;
            var invariant = System.Globalization.CultureInfo.InvariantCulture;
            string clean = (text ?? "").Trim().Replace(',', '.');
            string unit = String.IsNullOrEmpty(Unit) ? "" : " " + Unit;
            if (!Double.TryParse(clean, System.Globalization.NumberStyles.Float, invariant, out number) || Double.IsNaN(number) || Double.IsInfinity(number))
                return Name + " must be a number" + (unit.Length > 0 ? " (in" + unit + ")" : "") + ".";
            if ((Double.TryParse(Min, System.Globalization.NumberStyles.Float, invariant, out low) && number < low - 1e-9) ||
                (Double.TryParse(Max, System.Globalization.NumberStyles.Float, invariant, out high) && number > high + 1e-9))
                return Name + " must be between " + Min + " and " + Max + unit + ".";
            if (Integer && number != Math.Floor(number)) return Name + " must be a whole number.";
            value = number.ToString("0.######", invariant);
            return null;
        }

        /// <summary>Options no rule names are always offered; a named option shows while any of its rules holds.</summary>
        internal List<FrcOption> VisibleOptions(IDictionary<string, string> chosen, IList<FrcChoice> choices)
        {
            var controlled = new HashSet<string>();
            var shown = new HashSet<string>();
            foreach (var rule in OptionRules ?? new List<FrcOptionRule>())
            {
                bool holds = rule.VisibleWhen == null || rule.VisibleWhen.Holds(chosen, choices);
                foreach (string option in rule.Options ?? new List<string>())
                {
                    controlled.Add(option);
                    if (holds) shown.Add(option);
                }
            }
            return (Options ?? new List<FrcOption>()).Where(o => !controlled.Contains(o.Id) || shown.Contains(o.Id)).ToList();
        }
    }

    [DataContract]
    internal sealed class FrcItem
    {
        [DataMember(Name = "id")] public string Id { get; set; }
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "vendor")] public string Vendor { get; set; }
        [DataMember(Name = "group")] public string Group { get; set; }
        [DataMember(Name = "kind")] public string Kind { get; set; }
        [DataMember(Name = "configurable")] public bool Configurable { get; set; }
        [DataMember(Name = "partNumber")] public string PartNumber { get; set; }
        [DataMember(Name = "choices")] public List<FrcChoice> Choices { get; set; }
    }

    [DataContract]
    internal sealed class FrcClaim
    {
        [DataMember(Name = "status")] public string Status { get; set; }
        [DataMember(Name = "fingerprint")] public string Fingerprint { get; set; }
        [DataMember(Name = "token")] public string Token { get; set; }
        [DataMember(Name = "libraryPath")] public string LibraryPath { get; set; }
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "by")] public string By { get; set; }
    }

    [DataContract]
    internal sealed class FrcSearch
    {
        [DataMember(Name = "results")] public List<FrcItem> Results { get; set; }
    }

    /// <summary>
    /// The add-in's only link to FRCDesignLib: the JOCO server's adapter. No Onshape IDs or keys ever reach this side.
    /// Safe to call from background threads.
    /// </summary>
    internal sealed class FrcClient
    {
        private readonly NetworkCredential login;
        internal FrcClient(NetworkCredential login) { this.login = login; }

        internal static string CacheFolder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JocoRobos.Cad", "cache", "frcdesign"); }
        }

        internal List<FrcItem> Search(string query)
        {
            return Read<FrcSearch>(Send("GET", "search?q=" + Uri.EscapeDataString(query ?? ""), null)).Results ?? new List<FrcItem>();
        }

        internal FrcItem Details(string id)
        {
            return Read<FrcItem>(Send("GET", "item/" + Uri.EscapeDataString(id), null));
        }

        /// <summary>Small (70x40) or large (300x300) picture, cached on disk for two weeks.</summary>
        internal byte[] Thumbnail(string id, bool large)
        {
            string size = large ? "300x300" : "70x40";
            string cache = Path.Combine(CacheFolder, System.Text.RegularExpressions.Regex.Replace(id, "[^A-Za-z0-9-]", "_") + "-" + size + ".img");
            if (File.Exists(cache) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cache) < TimeSpan.FromDays(14)) return File.ReadAllBytes(cache);
            byte[] data = Send("GET", "thumb/" + Uri.EscapeDataString(id) + "?size=" + size, null);
            Directory.CreateDirectory(CacheFolder);
            File.WriteAllBytes(cache, data);
            return data;
        }

        /// <summary>Identifies this Windows user on this PC, so the same account on two computers can't both import one part.</summary>
        internal static string ClientId
        {
            get
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\JOCO ROBOS\CAD"))
                {
                    string id = key.GetValue("ClientId") as string;
                    if (String.IsNullOrEmpty(id))
                    {
                        id = Guid.NewGuid().ToString("N");
                        key.SetValue("ClientId", id, Microsoft.Win32.RegistryValueKind.String);
                    }
                    return id;
                }
            }
        }

        internal FrcClaim Claim(string id, IDictionary<string, string> configuration)
        {
            var body = new StringBuilder("{\"id\": ").Append(Json(id)).Append(", \"client\": ").Append(Json(ClientId)).Append(", \"configuration\": {");
            body.Append(String.Join(", ", configuration.Select(pair => Json(pair.Key) + ": " + Json(pair.Value)))).Append("}}");
            return Read<FrcClaim>(Send("POST", "claim", Encoding.UTF8.GetBytes(body.ToString())));
        }

        internal void Download(FrcClaim claim, string target)
        {
            byte[] data = Send("GET", "download/" + Uri.EscapeDataString(claim.Fingerprint), null, 300000, claim.Token);
            if (data.Length == 0) throw new InvalidOperationException("The server sent an empty file.");
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.WriteAllBytes(target, data);
        }

        internal void Complete(FrcClaim claim)
        {
            Send("POST", "complete", Encoding.UTF8.GetBytes("{\"fingerprint\": " + Json(claim.Fingerprint) + ", \"token\": " + Json(claim.Token) + "}"));
        }

        internal void Abandon(FrcClaim claim)
        {
            try { Send("POST", "abandon", Encoding.UTF8.GetBytes("{\"fingerprint\": " + Json(claim.Fingerprint) + ", \"token\": " + Json(claim.Token) + "}")); }
            catch (Exception exception) { System.Diagnostics.Trace.WriteLine("JOCO FRC abandon: " + exception.Message); }
        }

        private static string Json(string text)
        {
            var builder = new StringBuilder("\"");
            foreach (char c in text ?? "")
            {
                if (c == '"' || c == '\\') builder.Append('\\').Append(c);
                else if (c < ' ') builder.AppendFormat("\\u{0:x4}", (int)c);
                else builder.Append(c);
            }
            return builder.Append('"').ToString();
        }

        private static T Read<T>(byte[] data)
        {
            using (var stream = new MemoryStream(data))
                return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
        }

        private byte[] Send(string method, string path, byte[] body, int timeout = 60000, string token = null)
        {
            var request = (HttpWebRequest)WebRequest.Create(new Uri(WorkspaceInfo.Server, "admin/api/frcdesign/" + path));
            request.Method = method;
            request.Timeout = timeout;
            request.ReadWriteTimeout = timeout;
            request.AllowAutoRedirect = false;
            request.UserAgent = "JOCO-ROBOS-CAD";
            request.Headers["X-Joco-Client"] = "addin";
            if (token != null) request.Headers["X-Joco-Token"] = token;
            request.Headers[HttpRequestHeader.Authorization] = "Basic " +
                Convert.ToBase64String(Encoding.UTF8.GetBytes(login.UserName + ":" + login.Password));
            try
            {
                if (body != null)
                {
                    request.ContentType = "application/json";
                    request.ContentLength = body.Length;
                    using (var stream = request.GetRequestStream()) stream.Write(body, 0, body.Length);
                }
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var stream = response.GetResponseStream())
                using (var buffer = new MemoryStream())
                {
                    stream.CopyTo(buffer);
                    return buffer.ToArray();
                }
            }
            catch (WebException exception)
            {
                string problem = NetworkProblem.Describe(exception);
                if (problem != null) throw new InvalidOperationException("FRCDesignLib isn't available: " + problem + " Your robot was not changed.", exception);
                var response = (HttpWebResponse)exception.Response;
                using (response)
                using (var reader = new StreamReader(response.GetResponseStream()))
                {
                    string text = reader.ReadToEnd().Trim();
                    if (response.StatusCode == HttpStatusCode.Unauthorized) text = "The server did not accept your CAD username or password.";
                    throw new InvalidOperationException(text.Length > 0 && text.Length < 400 && !text.StartsWith("<") ? text : "FRCDesignLib request failed (" + (int)response.StatusCode + ").", exception);
                }
            }
        }
    }
}
