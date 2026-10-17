using System.Collections.Generic;
using System.Linq;

namespace FactoryLayout.Core
{
    /// <summary>
    /// Checks a conveyor route against the rules, written separately from the router so that the
    /// router is not only checked against itself. It also checks conveyors drawn by hand. Returns
    /// an empty list for a valid route.
    /// </summary>
    public static class RouteChecker
    {
        /// <param name="layout">The layout the route belongs to.</param>
        /// <param name="cells">The route, Start first.</param>
        /// <param name="stationOrder">If given, stations must be served in this order.</param>
        /// <param name="partial">
        /// A conveyor still being drawn: do not complain that it has not reached Stop or served
        /// every station yet, and allow it to end inside a tunnel or a drill's work cell.
        /// </param>
        public static List<string> Check(Layout layout, IList<Cell> cells, IList<string> stationOrder = null, bool partial = false)
        {
            var problems = new List<string>();
            var start = layout.OfKind(ComponentKind.Start).FirstOrDefault();
            var stop = layout.OfKind(ComponentKind.Stop).FirstOrDefault();
            if (start == null || stop == null) { problems.Add("layout has no Start or Stop"); return problems; }
            if (cells.Count < (partial ? 1 : 2)) { problems.Add("route has fewer than two cells"); return problems; }

            if (cells[0] != start.Origin) problems.Add("the conveyor does not begin at IN");
            if (!partial && cells[cells.Count - 1] != stop.Origin) problems.Add("the conveyor does not end at OUT");

            var seen = new HashSet<Cell>();
            foreach (var c in cells)
            {
                if (!layout.IsFloor(c)) problems.Add($"{Name(c)} is not floor");
                if (!seen.Add(c)) problems.Add($"the conveyor crosses itself at {Name(c)}");
            }

            for (int i = 1; i < cells.Count; i++)
            {
                if (Cell.Manhattan(cells[i - 1], cells[i]) != 1) { problems.Add($"{Name(cells[i - 1])} → {Name(cells[i])} is not one step"); continue; }
                if (i >= 2 && Cell.Manhattan(cells[i - 2], cells[i - 1]) == 1)
                {
                    var a = Dirs.Between(cells[i - 2], cells[i - 1]);
                    var b = Dirs.Between(cells[i - 1], cells[i]);
                    if (b == a.Opposite()) problems.Add($"U-turn at {Name(cells[i - 1])}");
                }
            }

            var index = new Dictionary<Cell, int>();
            for (int i = 0; i < cells.Count; i++) index[cells[i]] = i;
            int lastIdx = cells.Count - 1;

            // Bodies: only Start (first), Stop (last) and tunnels, straight along their axis.
            foreach (var comp in layout.Components)
            {
                var bodyOnRoute = comp.BodyCells().Where(index.ContainsKey).ToList();
                if (bodyOnRoute.Count == 0) continue;
                if (comp == start && bodyOnRoute.All(b => index[b] == 0)) continue;
                if (comp == stop && bodyOnRoute.All(b => index[b] == lastIdx)) continue;
                if (comp.Spec.Service != ServiceRule.PassThrough) { problems.Add($"the conveyor runs through {comp.Id} ({comp.DisplayName})"); continue; }

                var body = comp.BodyCells().ToList();
                bool endsInside = partial && bodyOnRoute.Any(b => index[b] == lastIdx);
                if (bodyOnRoute.Count != body.Count && !endsInside) { problems.Add($"the conveyor enters {comp.Id} but does not run through it"); continue; }
                var idx = body.Where(index.ContainsKey).Select(b => index[b]).ToList();
                bool forward = idx.Zip(idx.Skip(1), (p, q) => q - p).All(d => d == 1);
                bool backward = idx.Zip(idx.Skip(1), (p, q) => q - p).All(d => d == -1);
                if (!forward && !backward) { problems.Add($"the conveyor does not run straight through {comp.Id}"); continue; }
                int first = idx.Min(), last = idx.Max();
                if (first == 0) { problems.Add($"the conveyor starts inside {comp.Id}"); continue; }
                if (last == lastIdx && !partial) { problems.Add($"the conveyor ends inside {comp.Id}"); continue; }
                var travel = Dirs.Between(cells[first - 1], cells[first]);
                bool alongAxis = travel == comp.Facing || travel == comp.Facing.Opposite();
                bool straightIn = alongAxis && idx.Count >= 1;
                bool straightOut = last == lastIdx || Dirs.Between(cells[last], cells[last + 1]) == travel;
                if (!straightIn || !straightOut) problems.Add($"the conveyor turns inside or at the mouth of {comp.Id}");
            }

            // Drills: through the work cell, straight across the drill's face.
            foreach (var d in layout.Components.Where(c => c.Spec.Service == ServiceRule.Front))
            {
                if (!index.TryGetValue(d.FrontCell, out int i)) continue;
                if (i == 0 || (i == lastIdx && partial)) continue;
                if (i == lastIdx) { problems.Add($"the conveyor ends under {d.Id}"); continue; }
                var a = Dirs.Between(cells[i - 1], cells[i]);
                var b = Dirs.Between(cells[i], cells[i + 1]);
                if (a != b || a == d.Facing || a == d.Facing.Opposite())
                    problems.Add($"the conveyor must run straight past {d.Id}, across its face, not turn under it");
            }

            if (!partial)
            {
                foreach (var st in layout.Stations)
                    if (Hits(st, index).Count == 0) problems.Add($"{st.Id} ({st.DisplayName}) is not served by the conveyor");

                if (stationOrder != null && problems.Count == 0)
                {
                    // Each station must be served at or after the point where the previous one was.
                    int last = -1;
                    foreach (var id in stationOrder)
                    {
                        var st = layout.Find(id);
                        var after = Hits(st, index).Where(i => i >= last).ToList();
                        if (after.Count == 0) { problems.Add($"{id} is not served after the station before it"); break; }
                        last = after.Min();
                    }
                }
            }

            return problems;
        }

        /// <summary>Station ids in the order the conveyor first serves them.</summary>
        public static List<string> ServedStations(Layout layout, IList<Cell> cells)
        {
            var index = new Dictionary<Cell, int>();
            for (int i = 0; i < cells.Count; i++) if (!index.ContainsKey(cells[i])) index[cells[i]] = i;
            return layout.Stations
                .Select(s =>
                {
                    var h = Hits(s, index);
                    // A tunnel counts once the conveyor is through all of it.
                    if (s.Spec.Service == ServiceRule.PassThrough && h.Count < s.BodyCells().Count()) return (s.Id, -1);
                    return (s.Id, h.Count == 0 ? -1 : h.Min());
                })
                .Where(t => t.Item2 >= 0)
                .OrderBy(t => t.Item2)
                .Select(t => t.Item1)
                .ToList();
        }

        static List<int> Hits(Component st, Dictionary<Cell, int> index)
        {
            IEnumerable<Cell> want;
            switch (st.Spec.Service)
            {
                case ServiceRule.Envelope:
                case ServiceRule.Side: want = st.WorkCells(); break;
                case ServiceRule.Front: want = new[] { st.FrontCell }; break;
                case ServiceRule.PassThrough: want = st.BodyCells(); break;
                default: return new List<int>();
            }
            return want.Where(index.ContainsKey).Select(c => index[c]).ToList();
        }

        /// <summary>A cell as it is labelled on the drawing: column letter, row number.</summary>
        public static string Name(Cell c) => c.X >= 0 && c.Y >= 0 ? $"{BlueprintSvg.ColumnName(c.X)}{c.Y + 1}" : c.ToString();
    }
}
