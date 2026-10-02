using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace JocoRobos.Cad
{
    /// <summary>
    /// "Ask for it": a student asks whoever is editing a file to finish and give it back. The server keeps each request only
    /// while that person still holds the lock, so nothing here needs cleaning up.
    /// </summary>
    internal static class EditRequests
    {
        [DataContract]
        internal sealed class Request
        {
            [DataMember(Name = "id")] internal string Id;
            [DataMember(Name = "season")] internal string Season;
            [DataMember(Name = "path")] internal string Path;
            [DataMember(Name = "from")] internal string From;
        }

        [DataContract]
        private sealed class Waiting
        {
            [DataMember(Name = "requests")] internal List<Request> Requests;
        }

        /// <summary>Asks for a robot file (path relative to its season, with /). Returns who is editing it.</summary>
        internal static string Ask(NetworkCredential login, string season, string path)
        {
            return Accounts.Post("admin/api/edit-request", login, "{\"season\": " + Accounts.Json(season) + ", \"path\": " + Accounts.Json(path) + "}");
        }

        internal static void Dismiss(NetworkCredential login, string id)
        {
            Accounts.Post("admin/api/edit-request/dismiss", login, "{\"id\": " + Accounts.Json(id) + "}");
        }

        /// <summary>Requests waiting for this student (files they're editing that someone asked for).</summary>
        internal static List<Request> ForMe(NetworkCredential login)
        {
            var request = (HttpWebRequest)WebRequest.Create(new Uri(WorkspaceInfo.Server, "admin/api/edit-requests"));
            request.Timeout = 15000;
            request.AllowAutoRedirect = false;
            request.UserAgent = "JOCO-ROBOS-CAD";
            request.Headers["X-Joco-Client"] = "addin";
            request.Headers[HttpRequestHeader.Authorization] = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(login.UserName + ":" + login.Password));
            using (var response = request.GetResponse())
            using (var stream = response.GetResponseStream())
                return Parse(stream);
        }

        internal static List<Request> Parse(Stream stream)
        {
            var waiting = (Waiting)new DataContractJsonSerializer(typeof(Waiting)).ReadObject(stream);
            var result = new List<Request>();
            foreach (var r in waiting?.Requests ?? new List<Request>())
                if (r != null && !String.IsNullOrEmpty(r.Id) && WorkspacePolicy.IsRepositoryName(r.Season ?? "") && !String.IsNullOrEmpty(r.Path) &&
                    !r.Path.Contains("..") && !String.IsNullOrEmpty(r.From))
                    result.Add(r);
            return result;
        }
    }
}
