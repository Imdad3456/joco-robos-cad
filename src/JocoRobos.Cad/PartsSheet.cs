using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace JocoRobos.Cad
{
    /// <summary>One component instance in an assembly, as read from SOLIDWORKS (already without suppressed or excluded ones).</summary>
    internal sealed class PartUse
    {
        internal string Path;
        internal string Configuration;
    }

    internal sealed class PartsRow
    {
        internal bool Buy;
        internal string Vendor = "";
        internal string PartNumber = "";
        internal string Name = "";
        internal string Configuration = "";
        internal int Quantity;
        internal string Folder = "";

        internal string Key { get { return (Folder + "/" + Name + "|" + Configuration).ToLowerInvariant(); } }
    }

    /// <summary>
    /// The robot's parts list: what to buy (by vendor, with part numbers) and what the team makes, with quantities.
    /// Pure, so it's tested directly; the add-in only walks the assembly in SOLIDWORKS and hands over the component uses.
    /// </summary>
    internal static class PartsSheet
    {
        private const string Cots = "90_COTS";
        // Vendor part numbers as they appear in file names.
        // Vendor part numbers as they appear in file names ("_" and spaces both separate them from the rest of the name).
        private const string Start = @"(?<![A-Za-z0-9])", End = @"(?![A-Za-z0-9])";
        private static readonly Tuple<string, Regex>[] Numbers =
        {
            Tuple.Create("AndyMark", new Regex(Start + @"am-\d{3,5}[a-z]?" + End, RegexOptions.IgnoreCase)),
            Tuple.Create("WCP", new Regex(Start + @"WCP-\d{4}(-\d{3})?" + End, RegexOptions.IgnoreCase)),
            Tuple.Create("REV", new Regex(Start + @"REV-\d{2}-\d{4}(-[A-Z]\d{2})?" + End, RegexOptions.IgnoreCase)),
            Tuple.Create("McMaster-Carr", new Regex(Start + @"\d{4,5}[A-Z]\d{2,4}" + End)),
            Tuple.Create("VEX", new Regex(Start + @"217-\d{4}" + End)),
            Tuple.Create("TTB", new Regex(Start + @"TTB-\d{3,5}" + End, RegexOptions.IgnoreCase)),
        };

        /// <summary>A bought sub-assembly (gearbox, motor, sensor) counts as one item: everything in the COTS folder, plus vendor-numbered assemblies.</summary>
        internal static bool IsBoughtAssembly(string robotRoot, string path)
        {
            return InCots(robotRoot, path) || Numbers.Any(n => n.Item2.IsMatch(System.IO.Path.GetFileNameWithoutExtension(path) ?? ""));
        }

        private static bool InCots(string robotRoot, string path)
        {
            string cots = System.IO.Path.Combine(robotRoot, Cots) + System.IO.Path.DirectorySeparatorChar;
            return path.StartsWith(cots, StringComparison.OrdinalIgnoreCase);
        }

        internal static List<PartsRow> Build(string robotRoot, IEnumerable<PartUse> uses)
        {
            var rows = new Dictionary<string, PartsRow>();
            foreach (var use in uses)
            {
                var row = Describe(robotRoot, use);
                PartsRow existing;
                if (rows.TryGetValue(row.Key, out existing)) existing.Quantity++;
                else { row.Quantity = 1; rows[row.Key] = row; }
            }
            return rows.Values.OrderBy(r => r.Buy ? 0 : 1).ThenBy(r => r.Vendor, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Configuration, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static PartsRow Describe(string robotRoot, PartUse use)
        {
            string file = System.IO.Path.GetFileName(use.Path) ?? "";
            // Virtual components ("Bracket^Shooter.SLDPRT") are team-made parts saved inside their assembly.
            int virtualMark = file.IndexOf('^');
            string name = virtualMark > 0 ? file.Substring(0, virtualMark) : System.IO.Path.GetFileNameWithoutExtension(file);
            string folder = "";
            try
            {
                string directory = System.IO.Path.GetDirectoryName(use.Path) ?? "";
                if (directory.StartsWith(robotRoot, StringComparison.OrdinalIgnoreCase))
                    folder = directory.Substring(robotRoot.Length).TrimStart(System.IO.Path.DirectorySeparatorChar).Replace('\\', '/');
            }
            catch (ArgumentException) { }
            string configuration = use.Configuration ?? "";
            if (configuration.Equals("Default", StringComparison.OrdinalIgnoreCase)) configuration = "";
            var row = new PartsRow { Name = name, Configuration = configuration, Folder = virtualMark > 0 ? folder + " (inside its assembly)" : folder };
            var number = Numbers.Select(n => Tuple.Create(n.Item1, n.Item2.Match(name))).FirstOrDefault(m => m.Item2.Success);
            if (number != null) row.PartNumber = number.Item2.Value;
            if (virtualMark < 0 && InCots(robotRoot, use.Path))
            {
                row.Buy = true;
                // The vendor folder under 90_COTS (AndyMark, WCP, REV, Hardware, Electrical…); a part number can name it more exactly.
                string vendorFolder = folder.Length > Cots.Length ? folder.Substring(Cots.Length + 1).Split('/')[0] : "";
                row.Vendor = number != null && (vendorFolder.Length == 0 || vendorFolder.Equals("Hardware", StringComparison.OrdinalIgnoreCase))
                    ? number.Item1 : vendorFolder.Length > 0 ? vendorFolder : "Other";
            }
            else if (virtualMark < 0 && number != null)
            {
                row.Buy = true;
                row.Vendor = number.Item1;
            }
            else row.Vendor = "Team-made";
            return row;
        }

        internal static string Csv(IEnumerable<PartsRow> rows)
        {
            var text = new StringBuilder("Type,Vendor,Part number,Name,Configuration,Quantity,Folder\r\n");
            foreach (var r in rows)
                text.Append(String.Join(",", new[] { r.Buy ? "Buy" : "Make", r.Vendor, r.PartNumber, r.Name, r.Configuration, r.Quantity.ToString(), r.Folder }.Select(Cell)))
                    .Append("\r\n");
            return text.ToString();
        }

        // Quoted when needed; a leading = + - @ would be read as a formula by Excel, so it gets a ' first.
        private static string Cell(string value)
        {
            value = value ?? "";
            if (value.Length > 0 && "=+-@".IndexOf(value[0]) >= 0) value = "'" + value;
            return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        }

        /// <summary>The list as JSON for the mentor page (rows only; the server compares it with the previous one).</summary>
        internal static string Json(string season, string assembly, IEnumerable<PartsRow> rows)
        {
            var text = new StringBuilder("{\"season\": ").Append(Accounts.Json(season)).Append(", \"assembly\": ").Append(Accounts.Json(assembly)).Append(", \"rows\": [");
            bool first = true;
            foreach (var r in rows)
            {
                if (!first) text.Append(", ");
                first = false;
                text.Append("{\"buy\": ").Append(r.Buy ? "true" : "false")
                    .Append(", \"vendor\": ").Append(Accounts.Json(r.Vendor)).Append(", \"number\": ").Append(Accounts.Json(r.PartNumber))
                    .Append(", \"name\": ").Append(Accounts.Json(r.Name)).Append(", \"config\": ").Append(Accounts.Json(r.Configuration))
                    .Append(", \"qty\": ").Append(r.Quantity).Append(", \"folder\": ").Append(Accounts.Json(r.Folder)).Append("}");
            }
            return text.Append("]}").ToString();
        }
    }
}
