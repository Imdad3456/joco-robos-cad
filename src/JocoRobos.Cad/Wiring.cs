using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace JocoRobos.Cad
{
    internal sealed class WireType
    {
        internal string Name, Gauge;
        internal double Diameter;     // inches, outside, for bundles
        internal bool Power, Can;
    }

    internal sealed class ConnectorType
    {
        internal string Name;
        internal bool Can, PowerIn, PowerOut, Battery;
    }

    /// <summary>A routed wire as the report sees it: its ends (device and connector names), type, and lengths in inches.</summary>
    internal sealed class WireInfo
    {
        internal string Id, From = "", To = "", FromDevice = "", ToDevice = "", Harness = "";
        internal int Type;
        internal double Routed, Slack; // slack as a fraction: 0.1 is 10%
        internal double CutLength { get { return Math.Round(Routed * (1 + Slack), 1); } }
    }

    /// <summary>A connector placed on a device: its device (component) name and connector type.</summary>
    internal sealed class ConnectorInfo
    {
        internal string Device, Label;
        internal int Type;
    }

    /// <summary>
    /// Robot wiring: wire and connector types, cut lengths, the harness table (CSV), and the CAN and power network checks.
    /// Pure, tested without SOLIDWORKS; the add-in stores wires and connectors in the assembly and asks this for the reports.
    /// </summary>
    internal static class Wiring
    {
        // Stored by index in assemblies: only ever add to the end.
        internal static readonly List<WireType> WireTypes = new List<WireType>
        {
            new WireType { Name = "Battery cable", Gauge = "6 AWG", Diameter = 0.30, Power = true },
            new WireType { Name = "6 AWG", Gauge = "6 AWG", Diameter = 0.30, Power = true },
            new WireType { Name = "10 AWG", Gauge = "10 AWG", Diameter = 0.20, Power = true },
            new WireType { Name = "12 AWG", Gauge = "12 AWG", Diameter = 0.17, Power = true },
            new WireType { Name = "18 AWG", Gauge = "18 AWG", Diameter = 0.09, Power = true },
            new WireType { Name = "CAN", Gauge = "22 AWG pair", Diameter = 0.12, Can = true },
            new WireType { Name = "Ethernet", Gauge = "Cat5e", Diameter = 0.24 },
            new WireType { Name = "PWM", Gauge = "3 × 22 AWG", Diameter = 0.12 },
            new WireType { Name = "Sensor cable", Gauge = "22–26 AWG", Diameter = 0.12 },
            new WireType { Name = "Custom", Gauge = "", Diameter = 0.15 },
        };

        // Stored by index in assemblies: only ever add to the end.
        internal static readonly List<ConnectorType> ConnectorTypes = new List<ConnectorType>
        {
            new ConnectorType { Name = "CAN IN", Can = true },
            new ConnectorType { Name = "CAN OUT", Can = true },
            new ConnectorType { Name = "Power In", PowerIn = true },
            new ConnectorType { Name = "Power Out", PowerOut = true },
            new ConnectorType { Name = "Motor Power", PowerIn = true },
            new ConnectorType { Name = "Battery", Battery = true, PowerOut = true },
            new ConnectorType { Name = "PWM" },
            new ConnectorType { Name = "Ethernet" },
            new ConnectorType { Name = "USB" },
            new ConnectorType { Name = "Sensor" },
        };

        internal static WireType Type(int index) { return WireTypes[Math.Max(0, Math.Min(WireTypes.Count - 1, index))]; }
        internal static ConnectorType Connector(int index) { return ConnectorTypes[Math.Max(0, Math.Min(ConnectorTypes.Count - 1, index))]; }

        /// <summary>The next free wire id: W1, W2, …</summary>
        internal static string NextId(IEnumerable<string> used)
        {
            var taken = new HashSet<string>(used, StringComparer.OrdinalIgnoreCase);
            for (int n = 1; ; n++) if (!taken.Contains("W" + n)) return "W" + n;
        }

        /// <summary>A bundle's approximate diameter: the wires' cross-sections packed loosely (about 75% fill).</summary>
        internal static double BundleDiameter(IEnumerable<WireType> wires)
        {
            double area = wires.Sum(w => w.Diameter * w.Diameter);
            return area <= 0 ? 0 : Math.Round(Math.Sqrt(area / 0.75), 2);
        }

        internal static readonly string[] TableColumns = { "Wire", "Harness", "From", "To", "Type", "Gauge", "Routed (in)", "Slack", "Cut length (in)" };

        internal static List<string[]> TableRows(IEnumerable<WireInfo> wires)
        {
            return wires.OrderBy(w => w.Harness).ThenBy(w => Number(w.Id)).ThenBy(w => w.Id).Select(w => new[]
            {
                w.Id, w.Harness, w.From, w.To, Type(w.Type).Name, Type(w.Type).Gauge,
                Math.Round(w.Routed, 1).ToString("0.0", CultureInfo.InvariantCulture), Math.Round(w.Slack * 100) + "%",
                w.CutLength.ToString("0.0", CultureInfo.InvariantCulture),
            }).ToList();
        }

        private static int Number(string id)
        {
            int n;
            return id != null && id.Length > 1 && int.TryParse(id.Substring(1), out n) ? n : int.MaxValue;
        }

        /// <summary>The table as CSV (quoted where needed), for a spreadsheet or the robot's wiring doc.</summary>
        internal static string Csv(IEnumerable<string[]> rows)
        {
            var text = new StringBuilder();
            foreach (var row in new[] { TableColumns }.Concat(rows))
                text.Append(String.Join(",", row.Select(Quote))).Append("\r\n");
            return text.ToString();
        }

        private static string Quote(string cell)
        {
            cell = cell ?? "";
            return cell.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0 ? cell : "\"" + cell.Replace("\"", "\"\"") + "\"";
        }

        /// <summary>
        /// The CAN network: devices with CAN connectors and the CAN wires between them. Reports the chain order (from the
        /// roboRIO when there is one), devices with no CAN wire, branches (a device wired to more than two others), and a
        /// network split into pieces. Lines of text, problems first.
        /// </summary>
        internal static List<string> CanReport(IList<ConnectorInfo> connectors, IList<WireInfo> wires)
        {
            var devices = connectors.Where(c => Connector(c.Type).Can).Select(c => c.Device).Distinct().ToList();
            var links = wires.Where(w => Type(w.Type).Can && w.FromDevice.Length > 0 && w.ToDevice.Length > 0 && w.FromDevice != w.ToDevice).ToList();
            foreach (var w in links) foreach (var d in new[] { w.FromDevice, w.ToDevice }) if (!devices.Contains(d)) devices.Add(d);
            var lines = new List<string>();
            if (devices.Count == 0) return new List<string> { "No CAN devices yet: add CAN IN / CAN OUT connectors to them, then route CAN wires between them." };
            var neighbors = devices.ToDictionary(d => d, d => links.Where(w => w.FromDevice == d || w.ToDevice == d).Select(w => w.FromDevice == d ? w.ToDevice : w.FromDevice).ToList());
            foreach (var d in devices.Where(d => neighbors[d].Count == 0)) lines.Add("✗ " + d + " has no CAN wire.");
            foreach (var d in devices.Where(d => neighbors[d].Count > 2)) lines.Add("✗ " + d + " branches to " + neighbors[d].Count + " devices (" + String.Join(", ", neighbors[d]) + "): CAN must be one chain.");
            var pieces = Pieces(devices.Where(d => neighbors[d].Count > 0).ToList(), neighbors);
            if (pieces.Count > 1) lines.Add("✗ The CAN chain is in " + pieces.Count + " separate pieces: connect them into one.");
            foreach (var piece in pieces)
            {
                if (piece.Any(d => neighbors[d].Count > 2)) continue;
                var ends = piece.Where(d => neighbors[d].Count == 1).ToList();
                if (ends.Count != 2) { lines.Add("✗ " + String.Join(", ", piece) + " are wired in a loop: CAN must be a chain with two ends."); continue; }
                string start = piece.FirstOrDefault(d => d.IndexOf("roborio", StringComparison.OrdinalIgnoreCase) >= 0 && neighbors[d].Count == 1) ?? ends[0];
                var order = new List<string> { start };
                while (true)
                {
                    var next = neighbors[order[order.Count - 1]].FirstOrDefault(n => !order.Contains(n));
                    if (next == null) break;
                    order.Add(next);
                }
                lines.Add("CAN chain (" + order.Count + " devices): " + String.Join(" → ", order) + ". Terminate the far end (" + order[order.Count - 1] + ").");
            }
            if (lines.All(l => !l.StartsWith("✗"))) lines.Insert(0, "✓ CAN is one chain.");
            return lines.OrderBy(l => l.StartsWith("✗") ? 0 : 1).ToList();
        }

        /// <summary>
        /// The power network: from each Battery connector along power wires (battery cable, 6–18 AWG). Devices with a Power In or
        /// Motor Power connector that no power wire reaches are listed.
        /// </summary>
        internal static List<string> PowerReport(IList<ConnectorInfo> connectors, IList<WireInfo> wires)
        {
            var links = wires.Where(w => Type(w.Type).Power && w.FromDevice.Length > 0 && w.ToDevice.Length > 0).ToList();
            var batteries = connectors.Where(c => Connector(c.Type).Battery).Select(c => c.Device).Distinct().ToList();
            var needs = connectors.Where(c => Connector(c.Type).PowerIn).Select(c => c.Device).Distinct().ToList();
            if (batteries.Count == 0) return new List<string> { "No Battery connector yet: add one to the battery, then route its battery cable." };
            var reached = new HashSet<string>(batteries);
            var queue = new Queue<string>(batteries);
            while (queue.Count > 0)
            {
                string device = queue.Dequeue();
                foreach (var w in links.Where(w => w.FromDevice == device || w.ToDevice == device))
                {
                    string other = w.FromDevice == device ? w.ToDevice : w.FromDevice;
                    if (reached.Add(other)) queue.Enqueue(other);
                }
            }
            var unpowered = needs.Where(d => !reached.Contains(d)).ToList();
            var lines = unpowered.Select(d => "✗ " + d + " isn't connected to the battery by power wires.").ToList();
            lines.Add((unpowered.Count == 0 ? "✓ " : "") + (reached.Count - batteries.Count) + " device" + (reached.Count - batteries.Count == 1 ? "" : "s") + " powered from the battery" +
                (reached.Count > batteries.Count ? ": " + String.Join(", ", reached.Where(d => !batteries.Contains(d)).OrderBy(d => d)) : "") + ".");
            return lines;
        }

        private static List<List<string>> Pieces(List<string> devices, Dictionary<string, List<string>> neighbors)
        {
            var pieces = new List<List<string>>();
            var seen = new HashSet<string>();
            foreach (var d in devices)
            {
                if (!seen.Add(d)) continue;
                var piece = new List<string> { d };
                var queue = new Queue<string>(new[] { d });
                while (queue.Count > 0)
                    foreach (var n in neighbors[queue.Dequeue()])
                        if (seen.Add(n)) { piece.Add(n); queue.Enqueue(n); }
                pieces.Add(piece);
            }
            return pieces;
        }
    }
}
