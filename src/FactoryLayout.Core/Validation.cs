using System.Collections.Generic;
using System.Linq;

namespace FactoryLayout.Core
{
    public enum Severity { Error, Warning }

    public sealed class Issue
    {
        public Severity Severity { get; }
        public string Code { get; }
        public string Message { get; }
        /// <summary>What to do about it, in one line, or null.</summary>
        public string Fix { get; }
        public IReadOnlyList<string> ComponentIds { get; }
        public IReadOnlyList<Cell> Cells { get; }

        public Issue(Severity severity, string code, string message, IEnumerable<string> ids = null, IEnumerable<Cell> cells = null,
            string fix = null)
        {
            Severity = severity; Code = code; Message = message; Fix = fix;
            ComponentIds = (ids ?? Enumerable.Empty<string>()).ToList();
            Cells = (cells ?? Enumerable.Empty<Cell>()).ToList();
        }

        public override string ToString() => $"{Severity} {Code}: {Message}";
    }

    /// <summary>
    /// Checks a layout before anything is routed or built. Errors block routing and CAD output;
    /// warnings are shown but allowed.
    /// </summary>
    public static class LayoutValidator
    {
        public static List<Issue> Validate(Layout layout)
        {
            var issues = new List<Issue>();
            if (layout.Width < 2 || layout.Height < 2)
                issues.Add(new Issue(Severity.Error, "E_FLOOR", "The floor must be at least 2×2 cells."));

            int starts = layout.OfKind(ComponentKind.Start).Count();
            int stops = layout.OfKind(ComponentKind.Stop).Count();
            if (starts != 1)
                issues.Add(new Issue(Severity.Error, "E_START_COUNT", starts == 0 ? "Place a Start." : "Only one Start is allowed.",
                    fix: starts == 0 ? "Pick Start and click where the products enter the line." : "Remove the extra Start."));
            if (stops != 1)
                issues.Add(new Issue(Severity.Error, "E_STOP_COUNT", stops == 0 ? "Place a Stop." : "Only one Stop is allowed.",
                    fix: stops == 0 ? "Pick Stop and click where the products leave the line." : "Remove the extra Stop."));

            // Bodies: on the floor, no overlaps.
            var owner = new Dictionary<Cell, Component>();
            foreach (var c in layout.Components)
            {
                var off = c.BodyCells().Where(b => !layout.IsFloor(b)).ToList();
                if (off.Count > 0)
                    issues.Add(new Issue(Severity.Error, "E_OUT_OF_BOUNDS", $"{c.Id} stands off the floor.", new[] { c.Id }, off,
                        "Move it onto the floor, or rotate it (R)."));
                foreach (var b in c.BodyCells())
                {
                    if (owner.TryGetValue(b, out var other))
                        issues.Add(new Issue(Severity.Error, "E_OVERLAP", $"{c.Id} overlaps {other.Id}.", new[] { c.Id, other.Id }, new[] { b },
                            $"Drag {c.Id} to a free spot."));
                    else owner[b] = c;
                }
            }

            // Work zones: a robot's envelope, a drill's work cell, a machine's loading side.
            // On the floor, free of other equipment, and not shared unless both are robots.
            var zoneOwner = new Dictionary<Cell, Component>();
            foreach (var c in layout.Components)
            {
                var zone = c.WorkCells().ToList();
                if (zone.Count == 0) continue;
                var off = zone.Where(e => !layout.IsFloor(e)).ToList();
                if (off.Count > 0)
                {
                    switch (c.Spec.Service)
                    {
                        case ServiceRule.Envelope:
                            issues.Add(new Issue(Severity.Error, "E_ENVELOPE_OUTSIDE",
                                $"{c.Id} is too close to the wall: a robot arm needs the 3×3 around it to move.", new[] { c.Id }, off,
                                $"Move {c.Id} at least one cell from the wall."));
                            break;
                        case ServiceRule.Front:
                            issues.Add(new Issue(Severity.Error, "E_FRONT_BLOCKED",
                                $"{c.Id} faces the wall; its work cell has to be on the floor, over the conveyor.", new[] { c.Id }, off,
                                $"Rotate {c.Id} (R) so it faces the line."));
                            break;
                        default:
                            issues.Add(new Issue(Severity.Error, "E_SIDE_BLOCKED",
                                $"{c.Id}'s loading side faces the wall.", new[] { c.Id }, off,
                                $"Rotate {c.Id} (R) so its loading side faces the line."));
                            break;
                    }
                }

                foreach (var e in zone.Where(layout.IsFloor))
                {
                    if (owner.TryGetValue(e, out var intruder) && intruder != c)
                    {
                        string code = c.Spec.Service == ServiceRule.Envelope ? "E_ENVELOPE_BLOCKED"
                            : c.Spec.Service == ServiceRule.Front ? "E_FRONT_BLOCKED" : "E_SIDE_BLOCKED";
                        string what = c.Spec.Service == ServiceRule.Envelope ? "work envelope"
                            : c.Spec.Service == ServiceRule.Front ? "work cell" : "loading side";
                        issues.Add(new Issue(Severity.Error, code, $"{intruder.Id} stands in {c.Id}'s {what}.",
                            new[] { c.Id, intruder.Id }, new[] { e }, $"Move {intruder.Id} or {c.Id} apart."));
                    }
                    if (zoneOwner.TryGetValue(e, out var shared) && shared != c)
                    {
                        if (c.Kind == ComponentKind.Robot && shared.Kind == ComponentKind.Robot)
                            issues.Add(new Issue(Severity.Warning, "W_ENVELOPES_OVERLAP",
                                $"{c.Id} and {shared.Id} share envelope cells: the arms can collide unless they are interlocked.",
                                new[] { c.Id, shared.Id }, new[] { e }, "Move one robot a cell further away, or plan an interlock."));
                        else
                            issues.Add(new Issue(Severity.Error, "E_WORKZONE_CONFLICT",
                                $"{c.Id} and {shared.Id} work in the same cell: {Collision(c, shared)}",
                                new[] { c.Id, shared.Id }, new[] { e }, $"Move {c.Id} or {shared.Id} so their work zones do not touch."));
                    }
                    else zoneOwner[e] = c;
                }
            }

            // Drill: the conveyor runs straight through the work cell, across the drill's face,
            // so the tiles on both sides of the work cell must be free (Start or Stop may be one).
            foreach (var c in layout.Components.Where(c => c.Spec.Service == ServiceRule.Front))
            {
                var f = c.FrontCell;
                if (!layout.IsFloor(f)) continue; // already reported
                var sides = new[] { f.Step(c.Facing.RotateCw()), f.Step(c.Facing.RotateCcw()) };
                var bad = sides.Where(e => !layout.IsFloor(e) || (owner.TryGetValue(e, out var o)
                    && o.Kind != ComponentKind.Start && o.Kind != ComponentKind.Stop)).ToList();
                if (bad.Count > 0)
                    issues.Add(new Issue(Severity.Error, "E_FRONT_PASSAGE",
                        $"The conveyor cannot pass straight under {c.Id}: a tile beside its work cell is taken or not floor.",
                        new[] { c.Id }, bad, $"Move {c.Id} or what stands beside its work cell."));
            }

            // X-ray: both tunnel ends must be floor and free.
            foreach (var c in layout.Components.Where(c => c.Spec.Service == ServiceRule.PassThrough))
            {
                var bad = new[] { c.BackCell, c.FrontCell }.Where(e => !layout.IsFloor(e) || owner.ContainsKey(e)).ToList();
                if (bad.Count > 0)
                    issues.Add(new Issue(Severity.Error, "E_PASSTHROUGH_BLOCKED",
                        $"{c.Id} needs a free cell at both ends of its tunnel so the conveyor can run through it.",
                        new[] { c.Id }, bad, $"Move or rotate {c.Id} (R)."));
            }

            return issues;
        }

        static string Collision(Component a, Component b)
        {
            var robot = a.Kind == ComponentKind.Robot ? a : b.Kind == ComponentKind.Robot ? b : null;
            var other = robot == a ? b : a;
            if (robot != null) return $"{robot.Id}'s arm would hit {other.Id} ({other.DisplayName}).";
            return "they would both work on the same product position.";
        }

        public static bool HasErrors(IEnumerable<Issue> issues) => issues.Any(i => i.Severity == Severity.Error);
    }
}
