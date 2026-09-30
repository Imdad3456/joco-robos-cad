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

        internal bool Holds(IDictionary<string, string> chosen)
        {
            string current;
            if (Mode == "equals") return chosen.TryGetValue(Id ?? "", out current) && current == Value;
            var results = (Children ?? new List<FrcCondition>()).Select(c => c.Holds(chosen)).ToList();
            return Mode == "any" ? results.Any(x => x) : results.All(x => x);
        }
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

        internal FrcClaim Claim(string id, IDictionary<string, string> configuration)
        {
            var body = new StringBuilder("{\"id\": ").Append(Json(id)).Append(", \"configuration\": {");
            body.Append(String.Join(", ", configuration.Select(pair => Json(pair.Key) + ": " + Json(pair.Value)))).Append("}}");
            return Read<FrcClaim>(Send("POST", "claim", Encoding.UTF8.GetBytes(body.ToString())));
        }

        internal void Download(string fingerprint, string target)
        {
            byte[] data = Send("GET", "download/" + Uri.EscapeDataString(fingerprint), null, 300000);
            if (data.Length == 0) throw new InvalidOperationException("The server sent an empty file.");
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.WriteAllBytes(target, data);
        }

        internal void Complete(string fingerprint)
        {
            Send("POST", "complete", Encoding.UTF8.GetBytes("{\"fingerprint\": " + Json(fingerprint) + "}"));
        }

        internal void Abandon(string fingerprint)
        {
            try { Send("POST", "abandon", Encoding.UTF8.GetBytes("{\"fingerprint\": " + Json(fingerprint) + "}")); }
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

        private byte[] Send(string method, string path, byte[] body, int timeout = 60000)
        {
            var request = (HttpWebRequest)WebRequest.Create(new Uri(WorkspaceInfo.Server, "admin/api/frcdesign/" + path));
            request.Method = method;
            request.Timeout = timeout;
            request.ReadWriteTimeout = timeout;
            request.AllowAutoRedirect = false;
            request.UserAgent = "JOCO-ROBOS-CAD";
            request.Headers["X-Joco-Client"] = "addin";
            request.Headers[HttpRequestHeader.Authorization] = "Basic " +
                Convert.ToBase64String(Encoding.UTF8.GetBytes(login.UserName + ":" + login.Password));
            if (body != null)
            {
                request.ContentType = "application/json";
                request.ContentLength = body.Length;
                using (var stream = request.GetRequestStream()) stream.Write(body, 0, body.Length);
            }
            try
            {
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
                var response = exception.Response as HttpWebResponse;
                if (response == null)
                    throw new InvalidOperationException("FRCDesignLib isn't available right now (can't reach the CAD server). Your robot was not changed.", exception);
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
