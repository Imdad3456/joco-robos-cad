using System;
using System.Text.RegularExpressions;

namespace JocoRobos.Cad
{
    /// <summary>Makes a support report safe to paste anywhere: no passwords, codes, tokens, or sign-in headers.</summary>
    internal static class Diagnostics
    {
        private static readonly Regex[] Secrets =
        {
            new Regex(@"(?i)authorization\s*[:=]\s*\S+(\s+\S+)?"),
            new Regex(@"(?i)\bbasic\s+[A-Za-z0-9+/=]{8,}"),
            new Regex(@"(?i)\b(password|passwd|pwd|secret|token|api[_-]?key)(\s*[:=]\s*|\s+is\s+)[^\s,;]+"),
            new Regex(@"(?i)\b(x-joco-token)\s*[:=]\s*\S+"),
            new Regex(@"\b[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}\b"), // Setup codes.
            new Regex(@"(?i)(https?://)[^/\s:@]+:[^/\s@]+@"),                     // user:password@ in a URL.
        };

        internal static string Sanitize(string text)
        {
            if (String.IsNullOrEmpty(text)) return text ?? "";
            foreach (var secret in Secrets)
                text = secret.Replace(text, m => m.Value.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? m.Groups[1].Value + "[removed]@" : "[removed]");
            return text;
        }
    }
}
