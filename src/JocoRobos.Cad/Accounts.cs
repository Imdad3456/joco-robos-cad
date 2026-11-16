using System;
using System.IO;
using System.Net;
using System.Text;

namespace JocoRobos.Cad
{
    /// <summary>Requests to the team server's small JSON APIs (sign-in, edit requests, parts lists).</summary>
    internal static class Accounts
    {
        /// <summary>POSTs JSON as this add-in and returns the server's answer; a refusal becomes an InvalidOperationException with its reason.</summary>
        internal static string Post(string path, NetworkCredential login, string json)
        {
            var request = (HttpWebRequest)WebRequest.Create(new Uri(WorkspaceInfo.Server, path));
            request.Method = "POST";
            request.Timeout = 30000;
            request.AllowAutoRedirect = false;
            request.ContentType = "application/json";
            request.UserAgent = "JOCO-ROBOS-CAD";
            request.Headers["X-Joco-Client"] = "addin";
            if (login != null)
                request.Headers[HttpRequestHeader.Authorization] = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(login.UserName + ":" + login.Password));
            byte[] body = Encoding.UTF8.GetBytes(json);
            try
            {
                using (var stream = request.GetRequestStream()) stream.Write(body, 0, body.Length);
                using (var response = request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream()))
                    return reader.ReadToEnd().Trim();
            }
            catch (WebException exception)
            {
                string problem = NetworkProblem.Describe(exception);
                if (problem != null) throw new InvalidOperationException(problem, exception);
                var response = (HttpWebResponse)exception.Response;
                using (response)
                using (var reader = new StreamReader(response.GetResponseStream()))
                {
                    string text = reader.ReadToEnd().Trim();
                    if (response.StatusCode == HttpStatusCode.Unauthorized) text = "The server didn't accept your sign-in. Sign in again (Tools → CAD Hub → Sign In).";
                    throw new InvalidOperationException(text.Length > 0 && text.Length < 300 && !text.StartsWith("<") ? text : "The server refused (" + (int)response.StatusCode + ").", exception);
                }
            }
        }

        internal static string Json(string text)
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
    }
}
