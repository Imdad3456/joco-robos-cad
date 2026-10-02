using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace JocoRobos.Cad
{
    /// <summary>How long CAD Hub's own operations take on this computer (this session), for Diagnostics: what to make faster.</summary>
    internal static class Timings
    {
        private sealed class Entry { internal long Last, Slowest, Total; internal int Count; }
        private static readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();

        internal static void Record(string what, long milliseconds)
        {
            lock (entries)
            {
                Entry entry;
                if (!entries.TryGetValue(what, out entry)) entries[what] = entry = new Entry();
                entry.Last = milliseconds;
                entry.Slowest = Math.Max(entry.Slowest, milliseconds);
                entry.Total += milliseconds;
                entry.Count++;
            }
        }

        internal static string Report()
        {
            lock (entries)
            {
                if (entries.Count == 0) return "(nothing measured yet this session)\r\n";
                var text = new StringBuilder();
                foreach (var pair in entries.OrderByDescending(p => p.Value.Slowest))
                    text.Append("  ").Append(pair.Key).Append(": last ").Append(Seconds(pair.Value.Last)).Append(", slowest ").Append(Seconds(pair.Value.Slowest))
                        .Append(", average ").Append(Seconds(pair.Value.Total / pair.Value.Count)).Append(" (").Append(pair.Value.Count).Append("×)\r\n");
                return text.ToString();
            }
        }

        private static string Seconds(long milliseconds)
        {
            return (milliseconds / 1000.0).ToString("0.0") + " s";
        }
    }
}
