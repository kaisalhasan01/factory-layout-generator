using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace FactoryLayout.Core
{
    /// <summary>
    /// Draws a layout as a blueprint: the same symbols as the Unity editor's blueprint view, as a
    /// standalone SVG for documents and reviews.
    /// </summary>
    public static class BlueprintSvg
    {
        const double S = 44;       // px per cell
        const double M = 56;       // margin for axis labels
        const string Paper = "#123f6e";
        const string Ink = "#e8f1ff";
        const string Faint = "#5f86b3";
        const string Warn = "#ffcf5a";
        const string Bad = "#ff6b6b";

        public static string Render(Layout layout, RouteResult route = null, IList<Issue> issues = null)
        {
            issues = issues ?? LayoutValidator.Validate(layout);
            int W = layout.Width, H = layout.Height;
            double width = M + W * S + 24, titleH = 118, height = M + H * S + 24 + titleH;
            var sb = new StringBuilder();
            sb.Append($"<svg xmlns='http://www.w3.org/2000/svg' width='{F(width)}' height='{F(height)}' viewBox='0 0 {F(width)} {F(height)}' font-family='Consolas, monospace'>\n");
            sb.Append("<defs><pattern id='hatch' width='8' height='8' patternUnits='userSpaceOnUse' patternTransform='rotate(45)'>"
                      + $"<line x1='0' y1='0' x2='0' y2='8' stroke='{Warn}' stroke-width='1.2' stroke-opacity='0.55'/></pattern></defs>\n");
            sb.Append($"<rect width='100%' height='100%' fill='{Paper}'/>\n");

            // Grid, axes.
            for (int x = 0; x <= W; x++)
                sb.Append(Line(X(x), Y(0), X(x), Y(H), Faint, x % 5 == 0 ? 1.0 : 0.5));
            for (int y = 0; y <= H; y++)
                sb.Append(Line(X(0), Y(y), X(W), Y(y), Faint, y % 5 == 0 ? 1.0 : 0.5));
            sb.Append($"<rect x='{F(X(0))}' y='{F(Y(H))}' width='{F(W * S)}' height='{F(H * S)}' fill='none' stroke='{Ink}' stroke-width='2'/>\n");
            for (int x = 0; x < W; x++) sb.Append(Text(X(x + 0.5), Y(0) + 18, ColumnName(x), 12, Ink, "middle"));
            for (int y = 0; y < H; y++) sb.Append(Text(X(0) - 14, Y(y + 0.5) + 4, (y + 1).ToString(), 12, Ink, "middle"));
            sb.Append(Text(X(0), Y(H) - 10, $"{W * layout.CellMm / 1000.0:0.#} m × {H * layout.CellMm / 1000.0:0.#} m — tile {layout.CellMm} mm", 12, Ink, "start"));

            // Cells that are not floor: blacked out and crossed.
            foreach (var v in layout.Voids.Where(layout.InBounds))
            {
                sb.Append($"<rect x='{F(X(v.X))}' y='{F(Y(v.Y + 1))}' width='{F(S)}' height='{F(S)}' fill='#0a2340'/>");
                sb.Append(Line(X(v.X), Y(v.Y + 1), X(v.X + 1), Y(v.Y), Faint, 0.6) + "\n");
            }

            // Drill work cells and machine loading sides.
            foreach (var c in layout.Components.Where(c => c.Spec.Service == ServiceRule.Front || c.Spec.Service == ServiceRule.Side))
                foreach (var e in c.WorkCells().Where(layout.IsFloor))
                    sb.Append($"<rect x='{F(X(e.X) + 2)}' y='{F(Y(e.Y + 1) + 2)}' width='{F(S - 4)}' height='{F(S - 4)}' fill='url(#hatch)' stroke='{Warn}' stroke-width='1' stroke-dasharray='3 3'/>\n");

            // Work envelopes under everything else.
            foreach (var c in layout.Components.Where(c => c.Spec.Reach > 0))
            {
                var cells = c.EnvelopeCells().Concat(c.BodyCells()).ToList();
                int x0 = cells.Min(p => p.X), x1 = cells.Max(p => p.X) + 1, y0 = cells.Min(p => p.Y), y1 = cells.Max(p => p.Y) + 1;
                sb.Append($"<rect x='{F(X(x0))}' y='{F(Y(y1))}' width='{F((x1 - x0) * S)}' height='{F((y1 - y0) * S)}' fill='url(#hatch)' stroke='{Warn}' stroke-width='1.2' stroke-dasharray='6 4'/>\n");
                var ctr = Center(c.Origin);
                sb.Append($"<circle cx='{F(ctr.x)}' cy='{F(ctr.y)}' r='{F(S * (c.Spec.Reach + 0.5))}' fill='none' stroke='{Warn}' stroke-width='1' stroke-dasharray='2 4'/>\n");
            }

            // Conveyor.
            if (route != null && (route.Success || route.Manual) && route.Modules.Count > 1)
            {
                var path = ConveyorPath(route.Modules);
                sb.Append($"<path d='{path}' fill='none' stroke='{Ink}' stroke-width='{F(S * 0.62)}' stroke-linejoin='round'/>\n");
                sb.Append($"<path d='{path}' fill='none' stroke='{Paper}' stroke-width='{F(S * 0.62 - 4)}' stroke-linejoin='round'/>\n");
                sb.Append($"<path d='{path}' fill='none' stroke='{Faint}' stroke-width='1' stroke-dasharray='3 5'/>\n");
                foreach (var m in route.Modules.Where(m => m.Kind == ModuleKind.Straight && m.InsideStationId == null))
                    sb.Append(Chevron(Center(m.Cell), m.Out));
            }

            // Components.
            foreach (var c in layout.Components) sb.Append(Symbol(c, route));

            // Issue markers.
            foreach (var i in issues)
                foreach (var cell in i.Cells.Where(layout.InBounds))
                {
                    var col = i.Severity == Severity.Error ? Bad : Warn;
                    sb.Append($"<rect x='{F(X(cell.X) + 3)}' y='{F(Y(cell.Y + 1) + 3)}' width='{F(S - 6)}' height='{F(S - 6)}' fill='none' stroke='{col}' stroke-width='2.5'/>\n");
                }

            // Title block.
            double ty = M + H * S + 30;
            sb.Append($"<rect x='{F(M)}' y='{F(ty)}' width='{F(W * S)}' height='{F(titleH - 12)}' fill='none' stroke='{Ink}' stroke-width='1.5'/>\n");
            var m2 = LayoutMetrics.Of(layout, route);
            var lines = new List<string> { $"FACTORY LAYOUT  {layout.Name.ToUpperInvariant()}" };
            if (route != null && route.Success)
            {
                lines.Add($"Conveyor {m2.ConveyorLengthM:0.0} m   straight {m2.Straights}  left {m2.Lefts}  right {m2.Rights}   CAD instances {m2.CadInstances}");
                lines.Add($"Process order: IN → {string.Join(" → ", route.StationOrder)} → OUT   ({route.OrderMethod})");
            }
            else if (route != null) lines.Add("NOT ROUTED: " + route.Failure);
            var errs = issues.Where(i => i.Severity == Severity.Error).ToList();
            var warns = issues.Where(i => i.Severity == Severity.Warning).ToList();
            lines.Add(errs.Count == 0 ? (warns.Count == 0 ? "Checks: no errors, no warnings" : "Warning: " + warns[0].Message)
                                      : "Error: " + errs[0].Message);
            for (int i = 0; i < lines.Count; i++)
            {
                var col = i == lines.Count - 1 && errs.Count > 0 ? Bad : i == lines.Count - 1 && warns.Count > 0 ? Warn : Ink;
                sb.Append(Text(M + 12, ty + 24 + i * 22, lines[i], i == 0 ? 15 : 12, col, "start"));
            }

            sb.Append("</svg>\n");
            return sb.ToString();

            double X(double gx) => M + gx * S;
            double Y(double gy) => M + (H - gy) * S;
            (double x, double y) Center(Cell c) => (X(c.X + 0.5), Y(c.Y + 0.5));

            string ConveyorPath(List<ConveyorModule> mods)
            {
                var p = new StringBuilder();
                var c0 = Center(mods[0].Cell);
                p.Append($"M {F(c0.x)} {F(c0.y)} ");
                foreach (var m in mods.Skip(1))
                {
                    var c = Center(m.Cell);
                    if (m.IsTurn)
                    {
                        var a = Edge(c, m.In.Opposite());
                        var b = Edge(c, m.Out);
                        int sweep = m.Kind == ModuleKind.Left ? 0 : 1;
                        p.Append($"L {F(a.x)} {F(a.y)} A {F(S / 2)} {F(S / 2)} 0 0 {sweep} {F(b.x)} {F(b.y)} ");
                    }
                    else p.Append($"L {F(c.x)} {F(c.y)} ");
                }
                return p.ToString();
            }

            (double x, double y) Edge((double x, double y) c, Dir d) => (c.x + d.Dx() * S / 2, c.y - d.Dy() * S / 2);

            string Chevron((double x, double y) c, Dir d)
            {
                double fx = d.Dx(), fy = -d.Dy(), lx = -fy, ly = fx, k = S * 0.13;
                return $"<polyline points='{F(c.x - fx * k + lx * k)},{F(c.y - fy * k + ly * k)} {F(c.x + fx * k)},{F(c.y + fy * k)} {F(c.x - fx * k - lx * k)},{F(c.y - fy * k - ly * k)}' fill='none' stroke='{Ink}' stroke-width='1.5'/>\n";
            }

            string Symbol(Component c, RouteResult r)
            {
                var s = new StringBuilder();
                var ctr = Center(c.Origin);
                var body = c.BodyCells().ToList();
                int bx0 = body.Min(p => p.X), bx1 = body.Max(p => p.X) + 1, by0 = body.Min(p => p.Y), by1 = body.Max(p => p.Y) + 1;
                double rx = X(bx0) + 5, ry = Y(by1) + 5, rw = (bx1 - bx0) * S - 10, rh = (by1 - by0) * S - 10;
                switch (c.Kind)
                {
                    case ComponentKind.Pillar:
                        s.Append($"<rect x='{F(rx)}' y='{F(ry)}' width='{F(rw)}' height='{F(rh)}' fill='{Faint}' stroke='{Ink}' stroke-width='1.5'/>");
                        s.Append(Line(rx, ry, rx + rw, ry + rh, Ink, 1)).Append(Line(rx + rw, ry, rx, ry + rh, Ink, 1));
                        break;
                    case ComponentKind.Robot:
                        s.Append($"<circle cx='{F(ctr.x)}' cy='{F(ctr.y)}' r='{F(S * 0.32)}' fill='{Paper}' stroke='{Ink}' stroke-width='2'/>");
                        s.Append($"<circle cx='{F(ctr.x)}' cy='{F(ctr.y)}' r='{F(S * 0.1)}' fill='{Ink}'/>");
                        var f = Edge(ctr, c.Facing);
                        s.Append(Line(ctr.x, ctr.y, ctr.x + (f.x - ctr.x) * 0.75, ctr.y + (f.y - ctr.y) * 0.75, Ink, 3));
                        break;
                    case ComponentKind.Drill:
                        s.Append($"<rect x='{F(rx)}' y='{F(ry)}' width='{F(rw)}' height='{F(rh)}' fill='{Paper}' stroke='{Ink}' stroke-width='2'/>");
                        s.Append($"<circle cx='{F(ctr.x)}' cy='{F(ctr.y)}' r='{F(S * 0.16)}' fill='none' stroke='{Ink}' stroke-width='1.5'/>");
                        s.Append(Line(ctr.x - S * 0.25, ctr.y, ctr.x + S * 0.25, ctr.y, Ink, 0.8)).Append(Line(ctr.x, ctr.y - S * 0.25, ctr.x, ctr.y + S * 0.25, Ink, 0.8));
                        s.Append(Arrow(ctr, c.Facing, S * 0.75));
                        break;
                    case ComponentKind.XRay:
                        s.Append($"<rect x='{F(rx)}' y='{F(ry)}' width='{F(rw)}' height='{F(rh)}' fill='{Paper}' fill-opacity='0.6' stroke='{Ink}' stroke-width='2'/>");
                        s.Append($"<rect x='{F(rx + 4)}' y='{F(ry + 4)}' width='{F(rw - 8)}' height='{F(rh - 8)}' fill='none' stroke='{Ink}' stroke-width='0.8' stroke-dasharray='4 3'/>");
                        break;
                    case ComponentKind.Machine:
                    {
                        s.Append($"<rect x='{F(rx)}' y='{F(ry)}' width='{F(rw)}' height='{F(rh)}' fill='{Paper}' stroke='{Ink}' stroke-width='2.5'/>");
                        s.Append($"<rect x='{F(rx + 6)}' y='{F(ry + 6)}' width='{F(rw - 12)}' height='{F(rh - 12)}' fill='none' stroke='{Ink}' stroke-width='0.8'/>");
                        // The loading side: a heavy line along the face the conveyor passes.
                        var face = c.FaceCells().ToList();
                        var a = Edge(Center(face[0]), c.Facing.Opposite());
                        var b = Edge(Center(face[face.Count - 1]), c.Facing.Opposite());
                        var along = c.Facing.RotateCw();
                        double ax = along.Dx() * S * 0.42, ay = -along.Dy() * S * 0.42;
                        s.Append(Line(a.x - ax, a.y - ay, b.x + ax, b.y + ay, Warn, 4));
                        double mx = (rx + rx + rw) / 2, my = (ry + ry + rh) / 2;
                        s.Append(Text(mx, my + 4, c.DisplayName, 11, Ink, "middle"));
                        break;
                    }
                    case ComponentKind.Start:
                    case ComponentKind.Stop:
                        bool start = c.Kind == ComponentKind.Start;
                        s.Append($"<rect x='{F(rx)}' y='{F(ry)}' width='{F(rw)}' height='{F(rh)}' fill='{Paper}' stroke='{Ink}' stroke-width='2.5'/>");
                        s.Append(Text(ctr.x, ctr.y + 4, start ? "IN" : "OUT", 11, Ink, "middle"));
                        break;
                }
                if (c.Kind != ComponentKind.Start && c.Kind != ComponentKind.Stop)
                    s.Append(Text(X(bx1) - 3, Y(by1) + 11, c.Id, 9.5, Ink, "end"));
                return s.Append('\n').ToString();
            }

            string Arrow((double x, double y) from, Dir d, double len)
            {
                double fx = d.Dx(), fy = -d.Dy();
                double ex = from.x + fx * len, ey = from.y + fy * len, k = 5;
                return Line(from.x + fx * S * 0.2, from.y + fy * S * 0.2, ex, ey, Ink, 1.5)
                       + $"<polygon points='{F(ex + fx * k)},{F(ey + fy * k)} {F(ex - fy * k)},{F(ey + fx * k)} {F(ex + fy * k)},{F(ey - fx * k)}' fill='{Ink}'/>";
            }
        }

        public static string ColumnName(int x)
        {
            var s = "";
            x++;
            while (x > 0) { x--; s = (char)('A' + x % 26) + s; x /= 26; }
            return s;
        }

        static string Line(double x1, double y1, double x2, double y2, string col, double w) =>
            $"<line x1='{F(x1)}' y1='{F(y1)}' x2='{F(x2)}' y2='{F(y2)}' stroke='{col}' stroke-width='{F(w)}'/>";

        static string Text(double x, double y, string t, double size, string col, string anchor) =>
            $"<text x='{F(x)}' y='{F(y)}' font-size='{F(size)}' fill='{col}' text-anchor='{anchor}'>{Esc(t)}</text>\n";

        static string Esc(string t) => t.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        static string F(double v) => Math.Round(v, 2).ToString(CultureInfo.InvariantCulture);
    }
}
