using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace FactoryLayout.Core
{
    public sealed class RouteOptions
    {
        /// <summary>
        /// Extra cost of a 90° turn module, in cells of straight conveyor. Turns are dearer than
        /// straights (part cost, product transfer), so the router prefers fewer of them.
        /// </summary>
        public double TurnCost { get; set; } = 2.0;

        /// <summary>Search for the station order with the cheapest conveyor instead of using placement order.</summary>
        public bool OptimizeOrder { get; set; }

        /// <summary>Up to this many stations every order is tried; above it, a local search.</summary>
        public int ExhaustiveLimit { get; set; } = 6;

        /// <summary>
        /// How many endings per station the backtracking tries (the cheapest first). 1 is purely
        /// greedy: the cheapest path to each station, even if it walls off the next one.
        /// </summary>
        public int Branching { get; set; } = 8;

        /// <summary>Maximum segment searches per station order, so that a hopeless layout fails fast.</summary>
        public int SearchBudget { get; set; } = 20000;

        /// <summary>
        /// Wall-clock budget for the order search, so an infeasible layout cannot freeze the
        /// editor. The placement order is always tried in full first.
        /// </summary>
        public double OrderSearchMs { get; set; } = 400;
    }

    public enum ModuleKind { Start, Straight, Left, Right, Stop }

    /// <summary>
    /// One conveyor piece on one cell. Each module's outlet is the next one's inlet, which is the
    /// "instantiation upon instantiation" chain from the course project.
    /// </summary>
    public sealed class ConveyorModule
    {
        public ModuleKind Kind { get; set; }
        public Cell Cell { get; set; }
        /// <summary>Direction of travel entering the cell.</summary>
        public Dir In { get; set; }
        /// <summary>Direction of travel leaving the cell.</summary>
        public Dir Out { get; set; }
        /// <summary>Set when the module is the belt inside a station (the X-ray tunnel), not a separate part.</summary>
        public string InsideStationId { get; set; }

        public bool IsTurn => Kind == ModuleKind.Left || Kind == ModuleKind.Right;
        public override string ToString() => $"{Kind} {Cell} {In}->{Out}";
    }

    public sealed class RouteResult
    {
        public bool Success { get; set; }
        public string Failure { get; set; }
        public string FailedAtId { get; set; }
        public List<Cell> Cells { get; set; } = new List<Cell>();
        public List<ConveyorModule> Modules { get; set; } = new List<ConveyorModule>();
        /// <summary>Station ids in the order the conveyor serves them.</summary>
        public List<string> StationOrder { get; set; } = new List<string>();
        public double Cost { get; set; }
        public string OrderMethod { get; set; }
        public int OrdersTried { get; set; }
        public double ElapsedMs { get; set; }
        /// <summary>For a hand-drawn conveyor: every rule it breaks and what is still missing.</summary>
        public List<string> Problems { get; set; } = new List<string>();
        /// <summary>True for a hand-drawn conveyor.</summary>
        public bool Manual { get; set; }

        public int Turns => Modules.Count(m => m.IsTurn);

        public static RouteResult Fail(string why, string id = null) =>
            new RouteResult { Success = false, Failure = why, FailedAtId = id };
    }

    /// <summary>
    /// Routes the conveyor from Start, through every station in order, to Stop. Classical search,
    /// exact per segment: Dijkstra over (cell, heading) so that turns can be priced, with the
    /// conveyor already laid treated as an obstacle (the cable project's "trail").
    /// </summary>
    public static class ConveyorRouter
    {
        const int NoHeading = 4;

        sealed class Goal
        {
            public Cell Approach;
            public Dir? Forced;          // heading the conveyor must leave Approach in
            public List<Cell> Tail = new List<Cell>(); // cells it is then forced through
        }

        sealed class Segment
        {
            public List<Cell> Cells;     // excluding the cell it started from
            public int EndHeading;
            public bool EndsForced;
            public double Cost;
        }

        /// <summary>Where routing begins: Start, or the end of a hand-drawn piece.</summary>
        sealed class Prefix
        {
            public List<Cell> Cells;
            public int Heading;
            public bool StraightFirst;
        }

        /// <summary>
        /// The conveyor for this layout, whichever way it is made: routed when the layout's
        /// conveyor mode is Auto, checked when the user drew it.
        /// </summary>
        public static RouteResult Resolve(Layout layout, RouteOptions options = null) =>
            layout.ConveyorMode == ConveyorMode.Manual ? Evaluate(layout, layout.ManualPath) : Route(layout, options);

        /// <summary>Routes the whole conveyor from Start.</summary>
        public static RouteResult Route(Layout layout, RouteOptions options = null) => Route(layout, options, null);

        /// <summary>
        /// Routes on from the end of a hand-drawn piece (<paramref name="drawn"/>, starting at
        /// Start), through the stations it has not served yet, to Stop. The drawn cells are kept.
        /// </summary>
        public static RouteResult Complete(Layout layout, IList<Cell> drawn, RouteOptions options = null)
        {
            var start = layout.OfKind(ComponentKind.Start).FirstOrDefault();
            if (drawn == null || drawn.Count == 0 || start == null || drawn[0] != start.Origin)
                return Route(layout, options, null);
            var problems = RouteChecker.Check(layout, drawn, null, partial: true);
            if (problems.Count > 0) return RouteResult.Fail("Fix the drawn piece first: " + problems[0]);
            return Route(layout, options, drawn);
        }

        static RouteResult Route(Layout layout, RouteOptions options, IList<Cell> drawn)
        {
            options = options ?? new RouteOptions();
            var sw = Stopwatch.StartNew();

            var issues = LayoutValidator.Validate(layout);
            var firstError = issues.FirstOrDefault(i => i.Severity == Severity.Error);
            if (firstError != null)
            {
                var f = RouteResult.Fail("The layout has errors: " + firstError.Message, firstError.ComponentIds.FirstOrDefault());
                f.ElapsedMs = sw.Elapsed.TotalMilliseconds;
                return f;
            }

            Prefix prefix = null;
            var stations = layout.Stations.ToList();
            if (drawn != null && drawn.Count > 1)
            {
                var served = new HashSet<string>(RouteChecker.ServedStations(layout, drawn));
                stations = stations.Where(s => !served.Contains(s.Id)).ToList();
                var last = drawn[drawn.Count - 1];
                // Inside a tunnel or in a drill's work cell the belt has to carry on straight.
                bool mustGoStraight = layout.Components.Any(c =>
                    (c.Spec.Service == ServiceRule.PassThrough && c.BodyCells().Contains(last))
                    || (c.Spec.Service == ServiceRule.Front && c.FrontCell == last));
                prefix = new Prefix
                {
                    Cells = drawn.ToList(),
                    Heading = (int)Dirs.Between(drawn[drawn.Count - 2], last),
                    StraightFirst = mustGoStraight,
                };
            }

            RouteResult best;
            if (!options.OptimizeOrder || stations.Count < 2)
            {
                best = RouteOrder(layout, stations, options, double.PositiveInfinity, -1, prefix);
                best.OrderMethod = "placement order";
                best.OrdersTried = 1;
            }
            else if (stations.Count <= options.ExhaustiveLimit)
            {
                // The placement order with full backtracking, then every order greedily (cheap,
                // and pruned by the best cost so far). Full backtracking over every order costs
                // ~10× more for few extra successes.
                best = RouteOrder(layout, stations, options, double.PositiveInfinity, -1, prefix);
                int tried = 1;
                foreach (var order in Seq.Permutations(stations))
                {
                    if (sw.Elapsed.TotalMilliseconds > options.OrderSearchMs) break;
                    tried++;
                    var r = RouteOrder(layout, order, options, best.Success ? best.Cost : double.PositiveInfinity, 1, prefix);
                    if (r.Success && (!best.Success || r.Cost < best.Cost - 1e-9)) best = r;
                }
                if (!best.Success)
                    foreach (var order in Seq.Permutations(stations).Skip(1))
                    {
                        if (sw.Elapsed.TotalMilliseconds > options.OrderSearchMs) break;
                        tried++;
                        var r = RouteOrder(layout, order, options, double.PositiveInfinity, -1, prefix);
                        if (r.Success) { best = r; break; }
                    }
                best.OrderMethod = "all orders";
                best.OrdersTried = tried;
            }
            else
            {
                best = LocalSearch(layout, stations, options, prefix, sw, out int tried);
                best.OrderMethod = "local search";
                best.OrdersTried = tried;
            }

            if (best.Success && drawn != null)
                best.StationOrder = RouteChecker.ServedStations(layout, best.Cells);
            best.ElapsedMs = sw.Elapsed.TotalMilliseconds;
            return best;
        }

        /// <summary>
        /// Checks a hand-drawn conveyor. It counts as a finished conveyor only when it reaches
        /// Stop and breaks no rule; until then <see cref="RouteResult.Problems"/> says what is wrong
        /// or missing, and the modules drawn so far are still returned for display.
        /// </summary>
        public static RouteResult Evaluate(Layout layout, IList<Cell> path)
        {
            var sw = Stopwatch.StartNew();
            var result = new RouteResult { Manual = true, Cells = path.ToList(), OrderMethod = "drawn by hand", OrdersTried = 0 };
            var issues = LayoutValidator.Validate(layout);
            var err = issues.FirstOrDefault(i => i.Severity == Severity.Error);
            if (err != null) { result.Failure = "The layout has errors: " + err.Message; return result; }

            var start = layout.OfKind(ComponentKind.Start).First();
            var stop = layout.OfKind(ComponentKind.Stop).First();
            if (path.Count == 0 || path[0] != start.Origin)
            {
                result.Failure = "Draw the conveyor from Start: press on IN and drag.";
                result.Problems.Add(result.Failure);
                return result;
            }

            bool complete = path.Count > 1 && path[path.Count - 1] == stop.Origin;
            result.Problems = RouteChecker.Check(layout, path, null, partial: !complete);
            result.StationOrder = RouteChecker.ServedStations(layout, path);
            if (!complete)
            {
                var missing = layout.Stations.Where(s => !result.StationOrder.Contains(s.Id)).Select(s => s.Id).ToList();
                result.Problems.Add(missing.Count > 0
                    ? $"Not finished: still to serve {string.Join(", ", missing)}, then end at OUT."
                    : "Not finished: draw on to OUT.");
            }
            if (path.Count > 1) result.Modules = BuildModules(layout, path);
            result.Success = complete && result.Problems.Count == 0;
            result.Failure = result.Success ? null : result.Problems[0];
            result.Cost = path.Count - 1 + 2.0 * result.Turns;
            result.ElapsedMs = sw.Elapsed.TotalMilliseconds;
            return result;
        }

        /// <summary>Placement order and nearest-neighbour order, each improved by pairwise swaps.</summary>
        static RouteResult LocalSearch(Layout layout, List<Component> stations, RouteOptions options, Prefix prefix, Stopwatch sw, out int tried)
        {
            tried = 0;
            var at = prefix != null ? prefix.Cells[prefix.Cells.Count - 1] : layout.OfKind(ComponentKind.Start).First().Origin;
            var nn = new List<Component>();
            var left = new List<Component>(stations);
            while (left.Count > 0)
            {
                var from = at;
                var next = left.OrderBy(s => s.BodyCells().Min(b => Cell.Manhattan(b, from))).First();
                nn.Add(next); left.Remove(next); at = next.Origin;
            }

            RouteResult best = null;
            foreach (var seed in new[] { stations, nn })
            {
                var order = new List<Component>(seed);
                var cur = RouteOrder(layout, order, options, double.PositiveInfinity, -1, prefix); tried++;
                bool improved = true;
                int rounds = 0;
                while (improved && rounds++ < 20 && sw.Elapsed.TotalMilliseconds < options.OrderSearchMs)
                {
                    improved = false;
                    for (int i = 0; i < order.Count - 1; i++)
                        for (int j = i + 1; j < order.Count; j++)
                        {
                            var cand = new List<Component>(order);
                            (cand[i], cand[j]) = (cand[j], cand[i]);
                            var r = RouteOrder(layout, cand, options, cur.Success ? cur.Cost : double.PositiveInfinity, 1, prefix); tried++;
                            if (r.Success && (!cur.Success || r.Cost < cur.Cost - 1e-9)) { cur = r; order = cand; improved = true; }
                        }
                }
                if (best == null || (cur.Success && (!best.Success || cur.Cost < best.Cost - 1e-9))) best = cur;
            }
            return best;
        }

        /// <summary>
        /// Routes one fixed station order. Each segment's cheapest ending is tried first; if the
        /// conveyor then walls itself in further on, the next-cheapest endings are tried
        /// (depth-first backtracking, <paramref name="branching"/> endings per station).
        /// Gives up on branches whose cost passes <paramref name="bound"/>.
        /// </summary>
        static RouteResult RouteOrder(Layout layout, IList<Component> order, RouteOptions options, double bound,
            int branching, Prefix prefix)
        {
            if (branching < 0) branching = options.Branching;
            int w = layout.Width, h = layout.Height;
            var body = new bool[w * h];
            foreach (var c in layout.Components)
                foreach (var b in c.BodyCells())
                    if (layout.InBounds(b)) body[b.Y * w + b.X] = true;
            foreach (var v in layout.Voids)
                if (layout.InBounds(v)) body[v.Y * w + v.X] = true;

            var start = layout.OfKind(ComponentKind.Start).First();
            var stop = layout.OfKind(ComponentKind.Stop).First();

            var targets = new List<(Component station, List<Goal> goals, Cell? enter)>();
            foreach (var st in order) targets.Add((st, GoalsFor(st, layout, body), null));
            targets.Add((stop, new List<Goal> { new Goal { Approach = stop.Origin } }, stop.Origin));

            int budget = options.SearchBudget;
            int deepestFail = -1;
            bool pruned = false;
            List<Cell> found = null;
            double foundCost = 0;

            void Solve(int k, Cell cur, int heading, bool straightFirst, bool[] laid, List<Cell> cells, double cost)
            {
                if (k == targets.Count) { found = cells; foundCost = cost; return; }
                if (budget-- <= 0) return;

                var (station, goals, enter) = targets[k];
                var segs = Search(layout, body, laid, cur, heading, straightFirst, goals, enter, options.TurnCost, branching);
                if (segs.Count == 0) { deepestFail = Math.Max(deepestFail, k); return; }

                foreach (var seg in segs)
                {
                    if (cost + seg.Cost > bound) { pruned = true; continue; }
                    var seen = new HashSet<Cell>();
                    if (seg.Cells.Any(c => !seen.Add(c) || laid[c.Y * w + c.X])) continue; // would cross itself

                    var laid2 = (bool[])laid.Clone();
                    foreach (var c in seg.Cells) laid2[c.Y * w + c.X] = true;
                    var cells2 = new List<Cell>(cells);
                    cells2.AddRange(seg.Cells);
                    var end = seg.Cells.Count > 0 ? seg.Cells[seg.Cells.Count - 1] : cur;
                    Solve(k + 1, end, seg.EndHeading, seg.EndsForced, laid2, cells2, cost + seg.Cost);
                    if (found != null) return;
                }
                if (segs.Count > 0) deepestFail = Math.Max(deepestFail, k);
            }

            var laid0 = new bool[w * h];
            var cells0 = prefix != null ? new List<Cell>(prefix.Cells) : new List<Cell> { start.Origin };
            foreach (var c in cells0) laid0[c.Y * w + c.X] = true;
            Solve(0, cells0[cells0.Count - 1], prefix?.Heading ?? NoHeading, prefix?.StraightFirst ?? false, laid0, cells0, 0);

            if (found == null)
            {
                if (deepestFail < 0 && pruned) return RouteResult.Fail("pruned");
                var at = targets[Math.Max(0, deepestFail)].station;
                return RouteResult.Fail(at == stop
                    ? "No conveyor path reaches Stop without crossing itself or a component."
                    : $"No conveyor path reaches {at.Id} ({at.DisplayName}) in this process order.", at.Id);
            }

            return new RouteResult
            {
                Success = true,
                Cells = found,
                Cost = foundCost,
                StationOrder = order.Select(s => s.Id).ToList(),
                Modules = BuildModules(layout, found),
            };
        }

        static List<Goal> GoalsFor(Component st, Layout layout, bool[] body)
        {
            var goals = new List<Goal>();
            bool Free(Cell c) => layout.InBounds(c) && !body[c.Y * layout.Width + c.X];
            switch (st.Spec.Service)
            {
                case ServiceRule.Envelope:
                case ServiceRule.Side:
                    foreach (var e in st.WorkCells())
                        if (Free(e)) goals.Add(new Goal { Approach = e });
                    break;
                case ServiceRule.Front:
                    // Straight across the drill's face, through its work cell, either way.
                    foreach (var travel in new[] { st.Facing.RotateCw(), st.Facing.RotateCcw() })
                        goals.Add(new Goal
                        {
                            Approach = st.FrontCell.Step(travel.Opposite()),
                            Forced = travel,
                            Tail = new List<Cell> { st.FrontCell },
                        });
                    break;
                case ServiceRule.PassThrough:
                    foreach (var travel in new[] { st.Facing, st.Facing.Opposite() })
                        goals.Add(new Goal
                        {
                            Approach = travel == st.Facing ? st.BackCell : st.FrontCell,
                            Forced = travel,
                            Tail = st.BodyCellsAlong(travel),
                        });
                    break;
            }
            return goals;
        }

        /// <summary>
        /// Dijkstra from <paramref name="from"/> to the station's goals. Returns the cheapest path
        /// to each of the <paramref name="k"/> cheapest distinct goals, cheapest first.
        /// </summary>
        static List<Segment> Search(Layout layout, bool[] body, bool[] laid, Cell from, int h0, bool straightFirst,
            List<Goal> goals, Cell? enter, double turnCost, int k)
        {
            int w = layout.Width, h = layout.Height, n = w * h * 5;
            var dist = new double[n];
            var prev = new int[n];
            for (int i = 0; i < n; i++) { dist[i] = double.PositiveInfinity; prev[i] = -1; }

            var byCell = new Dictionary<Cell, List<Goal>>();
            foreach (var g in goals)
            {
                if (!layout.InBounds(g.Approach)) continue;
                if (!byCell.TryGetValue(g.Approach, out var list)) byCell[g.Approach] = list = new List<Goal>();
                list.Add(g);
            }
            var none = new List<Segment>();
            if (byCell.Count == 0) return none;

            int fromIdx = from.Y * w + from.X;
            int s0 = fromIdx * 5 + h0;
            dist[s0] = 0;
            var heap = new MinHeap();
            heap.Push(0, s0);

            // Best (cost, state) per goal; the search stops once k goals are known and nothing
            // left in the queue can beat the k-th of them.
            var hits = new Dictionary<Goal, (double cost, int state)>();
            double KthBest() => hits.Count < k ? double.PositiveInfinity
                : hits.Values.Select(v => v.cost).OrderBy(c => c).ElementAt(k - 1);
            double best = double.PositiveInfinity;

            while (heap.Count > 0)
            {
                heap.Pop(out double d, out int s);
                if (d > dist[s]) continue;
                if (d >= best) break;

                int ci = s / 5, hd = s % 5;
                var cell = new Cell(ci % w, ci / w);

                if (byCell.TryGetValue(cell, out var here))
                {
                    foreach (var g in here)
                    {
                        double extra = 0;
                        if (g.Forced.HasValue)
                        {
                            int f = (int)g.Forced.Value;
                            if (hd != NoHeading && f == (int)((Dir)hd).Opposite()) continue;
                            if (s == s0 && straightFirst && f != hd) continue;
                            if (hd != NoHeading && f != hd) extra += turnCost;
                            if (g.Tail.Any(t => !layout.InBounds(t) || laid[t.Y * w + t.X])) continue;
                            extra += g.Tail.Count;
                        }
                        if (!hits.TryGetValue(g, out var old) || d + extra < old.cost)
                        {
                            hits[g] = (d + extra, s);
                            best = KthBest();
                        }
                    }
                }

                // A body cell (a station or Stop) is never driven through, only started from.
                if (body[ci] && ci != fromIdx) continue;

                for (int nd = 0; nd < 4; nd++)
                {
                    if (hd != NoHeading && nd == (int)((Dir)hd).Opposite()) continue;
                    if (s == s0 && straightFirst && nd != hd) continue;
                    var nc = cell.Step((Dir)nd);
                    if (!layout.InBounds(nc)) continue;
                    int nci = nc.Y * w + nc.X;
                    if (laid[nci]) continue;
                    if (body[nci] && !(enter.HasValue && enter.Value == nc)) continue;
                    double nd2 = d + 1 + (hd != NoHeading && nd != hd ? turnCost : 0);
                    int ns = nci * 5 + nd;
                    if (nd2 < dist[ns]) { dist[ns] = nd2; prev[ns] = s; heap.Push(nd2, ns); }
                }
            }

            var result = new List<Segment>();
            foreach (var kv in hits.OrderBy(kv => kv.Value.cost).Take(k))
            {
                var goal = kv.Key;
                var path = new List<Cell>();
                for (int s = kv.Value.state; s != s0; s = prev[s])
                {
                    int ci = s / 5;
                    path.Add(new Cell(ci % w, ci / w));
                }
                path.Reverse();
                path.AddRange(goal.Tail);
                result.Add(new Segment
                {
                    Cells = path,
                    Cost = kv.Value.cost,
                    EndHeading = goal.Forced.HasValue ? (int)goal.Forced.Value : kv.Value.state % 5,
                    EndsForced = goal.Forced.HasValue,
                });
            }
            return result;
        }

        /// <summary>
        /// Turns a cell path into conveyor modules: Start, Straight/Left/Right, Stop. A path that
        /// does not end at Stop (one still being drawn) ends in a straight module.
        /// </summary>
        public static List<ConveyorModule> BuildModules(Layout layout, IList<Cell> cells)
        {
            var inside = new Dictionary<Cell, string>();
            foreach (var c in layout.Components.Where(c => c.Spec.Service == ServiceRule.PassThrough))
                foreach (var b in c.BodyCells()) inside[b] = c.Id;
            var start = layout.OfKind(ComponentKind.Start).FirstOrDefault();
            var stop = layout.OfKind(ComponentKind.Stop).FirstOrDefault();

            var modules = new List<ConveyorModule>();
            int n = cells.Count;
            for (int i = 0; i < n; i++)
            {
                if (i > 0 && Cell.Manhattan(cells[i - 1], cells[i]) != 1) break; // a broken hand-drawn path
                Dir dIn = i > 0 ? Dirs.Between(cells[i - 1], cells[i]) : (n > 1 && Cell.Manhattan(cells[0], cells[1]) == 1 ? Dirs.Between(cells[0], cells[1]) : Dir.East);
                Dir dOut = i < n - 1 && Cell.Manhattan(cells[i], cells[i + 1]) == 1 ? Dirs.Between(cells[i], cells[i + 1]) : dIn;
                ModuleKind kind;
                if (i == 0 && start != null && cells[0] == start.Origin) kind = ModuleKind.Start;
                else if (i == n - 1 && stop != null && cells[i] == stop.Origin) kind = ModuleKind.Stop;
                else if (dOut == dIn) kind = ModuleKind.Straight;
                else if (dOut == dIn.RotateCcw()) kind = ModuleKind.Left;
                else if (dOut == dIn.RotateCw()) kind = ModuleKind.Right;
                else kind = ModuleKind.Straight; // a U-turn, flagged by the checker

                modules.Add(new ConveyorModule
                {
                    Kind = kind, Cell = cells[i], In = dIn, Out = dOut,
                    InsideStationId = inside.TryGetValue(cells[i], out var id) ? id : null,
                });
            }
            return modules;
        }

        sealed class MinHeap
        {
            readonly List<(double k, int v)> _a = new List<(double, int)>();
            public int Count => _a.Count;

            public void Push(double k, int v)
            {
                _a.Add((k, v));
                int i = _a.Count - 1;
                while (i > 0)
                {
                    int p = (i - 1) / 2;
                    if (_a[p].k <= _a[i].k) break;
                    (_a[p], _a[i]) = (_a[i], _a[p]);
                    i = p;
                }
            }

            public void Pop(out double k, out int v)
            {
                (k, v) = _a[0];
                var last = _a[_a.Count - 1];
                _a.RemoveAt(_a.Count - 1);
                if (_a.Count == 0) return;
                _a[0] = last;
                int i = 0;
                while (true)
                {
                    int l = 2 * i + 1, r = l + 1, m = i;
                    if (l < _a.Count && _a[l].k < _a[m].k) m = l;
                    if (r < _a.Count && _a[r].k < _a[m].k) m = r;
                    if (m == i) break;
                    (_a[m], _a[i]) = (_a[i], _a[m]);
                    i = m;
                }
            }
        }
    }
}
