using System;
using System.Collections.Generic;
using System.Linq;

namespace JocoRobos.Cad
{
    /// <summary>
    /// Which assemblies use a file, all the way up to the robot, and what a file uses. Built from each assembly's direct
    /// references (read from SOLIDWORKS by the add-in, cached by save time). Pure, so it's tested directly.
    /// </summary>
    internal sealed class ReferenceGraph
    {
        private readonly Dictionary<string, List<string>> uses = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> usedBy = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        /// <param name="direct">Each assembly → the files it references directly.</param>
        internal ReferenceGraph(IDictionary<string, List<string>> direct)
        {
            foreach (var pair in direct)
            {
                var children = pair.Value.Where(x => !String.IsNullOrEmpty(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                uses[pair.Key] = children;
                foreach (string child in children)
                {
                    List<string> parents;
                    if (!usedBy.TryGetValue(child, out parents)) usedBy[child] = parents = new List<string>();
                    if (!parents.Contains(pair.Key, StringComparer.OrdinalIgnoreCase)) parents.Add(pair.Key);
                }
            }
        }

        internal List<string> Uses(string path)
        {
            List<string> list;
            return uses.TryGetValue(path, out list) ? list.OrderBy(System.IO.Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToList() : new List<string>();
        }

        /// <summary>Every assembly above the file, nearest first, with how many levels up it is (1 = uses it directly).</summary>
        internal List<Tuple<string, int>> UsedBy(string path)
        {
            var result = new List<Tuple<string, int>>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { path };
            var level = new List<string> { path };
            for (int depth = 1; level.Count > 0 && depth <= 50; depth++)
            {
                var next = new List<string>();
                foreach (string file in level)
                {
                    List<string> parents;
                    if (!usedBy.TryGetValue(file, out parents)) continue;
                    foreach (string parent in parents.OrderBy(System.IO.Path.GetFileName, StringComparer.OrdinalIgnoreCase))
                        if (seen.Add(parent)) { result.Add(Tuple.Create(parent, depth)); next.Add(parent); }
                }
                level = next;
            }
            return result;
        }
    }
}
