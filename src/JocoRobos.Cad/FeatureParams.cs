using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace JocoRobos.Cad
{
    /// <summary>
    /// A CAD Hub feature's settings as stored in the part ("teeth=36;pitch=20;…"), so Edit Feature reopens it with the same
    /// values and every rebuild makes the same geometry. Pure, so it's tested directly.
    /// </summary>
    internal sealed class FeatureParams
    {
        internal readonly Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal string this[string key]
        {
            get { string value; return Values.TryGetValue(key, out value) ? value : null; }
            set { Values[key] = value ?? ""; }
        }

        internal double Number(string key, double fallback)
        {
            double value;
            return double.TryParse(this[key], NumberStyles.Float, CultureInfo.InvariantCulture, out value) ? value : fallback;
        }

        internal void Set(string key, double value) { this[key] = value.ToString("R", CultureInfo.InvariantCulture); }

        internal string Encode()
        {
            return String.Join(";", Values.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => Escape(p.Key) + "=" + Escape(p.Value)));
        }

        internal static FeatureParams Decode(string text)
        {
            var result = new FeatureParams();
            foreach (string pair in (text ?? "").Split(';'))
            {
                int equals = pair.IndexOf('=');
                if (equals <= 0) continue;
                result.Values[Unescape(pair.Substring(0, equals))] = Unescape(pair.Substring(equals + 1));
            }
            return result;
        }

        private static string Escape(string text)
        {
            var builder = new StringBuilder();
            foreach (char c in text ?? "")
                builder.Append(c == '%' || c == ';' || c == '=' ? "%" + ((int)c).ToString("X2") : c.ToString());
            return builder.ToString();
        }

        private static string Unescape(string text)
        {
            var builder = new StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                int code;
                if (text[i] == '%' && i + 3 <= text.Length &&
                    int.TryParse(text.Substring(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                {
                    builder.Append((char)code);
                    i += 2;
                }
                else builder.Append(text[i]);
            }
            return builder.ToString();
        }
    }

    /// <summary>A bearing a Bearing Hole can be cut for: the team's first, then common FRC bearings.</summary>
    internal sealed class BearingPreset
    {
        internal string Name;
        internal double OutsideDiameter;
        internal bool Team;
    }

    /// <summary>The team's standards for CAD Hub's modeling tools (from the catalog), with defaults when mentors haven't set any.</summary>
    internal sealed class TeamStandards
    {
        internal double Easy = 0.0035, Normal = 0.0015, Press = 0.0005;
        internal readonly List<BearingPreset> Bearings = new List<BearingPreset>();

        internal static readonly TeamStandards Defaults = new TeamStandards();
    }

    internal static class BearingHoles
    {
        internal static readonly string[] Fits = { "Normal fit", "Press fit", "Easy fit" };

        // Common FRC bearings by outside diameter (the bore is what the hole is cut for).
        private static readonly BearingPreset[] Common =
        {
            new BearingPreset { Name = "1/2\" hex bearing (1.125\" OD)", OutsideDiameter = 1.125 },
            new BearingPreset { Name = "FR8ZZ / R8, 1/2\" round (1.125\" OD)", OutsideDiameter = 1.125 },
            new BearingPreset { Name = "3/8\" hex bearing (0.875\" OD)", OutsideDiameter = 0.875 },
            new BearingPreset { Name = "R6, 3/8\" round (0.875\" OD)", OutsideDiameter = 0.875 },
            new BearingPreset { Name = "R4, 1/4\" round (0.625\" OD)", OutsideDiameter = 0.625 },
            new BearingPreset { Name = "1.375\" OD bearing (swerve, thin section)", OutsideDiameter = 1.375 },
            new BearingPreset { Name = "6803, 17 mm (26 mm OD)", OutsideDiameter = 26 / 25.4 },
            new BearingPreset { Name = "6805, 25 mm (37 mm OD)", OutsideDiameter = 37 / 25.4 },
        };

        /// <summary>The team's bearings first (from Team Standards), then the common ones.</summary>
        internal static List<BearingPreset> Presets(TeamStandards standards)
        {
            return (standards ?? TeamStandards.Defaults).Bearings.Select(b => new BearingPreset { Name = "★ " + b.Name, OutsideDiameter = b.OutsideDiameter, Team = true })
                .Concat(Common).ToList();
        }

        /// <summary>The hole's diameter for a bearing and a fit name: the outside diameter plus the team's clearance for that fit.</summary>
        internal static double Bore(double outsideDiameter, string fit, TeamStandards standards)
        {
            var s = standards ?? TeamStandards.Defaults;
            double clearance = fit == "Press fit" ? s.Press : fit == "Easy fit" ? s.Easy : s.Normal;
            return Math.Round(outsideDiameter + clearance, 4);
        }
    }
}
