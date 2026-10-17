using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using FactoryLayout.Core;

namespace FactoryLayout.Catia
{
    /// <summary>
    /// Builds a layout.json in CATIA V5 through its COM automation API.
    ///
    /// The engine re-validates and re-routes the layout, and <see cref="CadPlan"/> lists every part
    /// with an origin point and a direction. In one new CATPart the builder then creates those
    /// inputs as geometry (a point and a line per part) and instantiates each part's PowerCopy
    /// template on them. A part whose template is missing is drawn as a plain block instead, so
    /// the builder always produces a model.
    ///
    /// UNVERIFIED: written and compiled without CATIA available. The plan (--dry-run) is tested;
    /// the COM calls follow the course's working VB.NET code and the V5 automation reference
    /// but have not been run. See catia/README.md.
    /// </summary>
    internal static class Program
    {
        const string Usage = @"FactoryLayout.Catia <layout.json> [options]

  --templates <dir>   folder with <Template>.CATPart files, each holding a PowerCopy
                      named PC_<Template> with inputs Origin, Direction, Floor
                      (default: a 'templates' folder next to the layout)
  --boxes             ignore templates; draw every part as a block
  --out <file>        save the CATPart here (default: <layout>.CATPart next to the layout)
  --optimize          let the router pick the station order
  --no-grid           skip the floor grid lines
  --dry-run           print the plan and write <layout>.plan.csv; does not need CATIA";

        [STAThread]
        static int Main(string[] args)
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            if (args.Length == 0 || args[0].StartsWith("-")) { Console.WriteLine(Usage); return 1; }

            string Opt(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
            var layoutPath = Path.GetFullPath(args[0]);
            var layout = LayoutJson.Read(File.ReadAllText(layoutPath));
            var issues = LayoutValidator.Validate(layout);
            foreach (var i in issues) Console.WriteLine("  " + i);
            if (LayoutValidator.HasErrors(issues)) { Console.WriteLine("The layout has errors; fix them in the editor first."); return 2; }

            var route = ConveyorRouter.Route(layout, new RouteOptions { OptimizeOrder = args.Contains("--optimize") });
            if (!route.Success) Console.WriteLine("  conveyor not routed: " + route.Failure + " (building components only)");
            var plan = CadPlan.From(layout, route);
            Console.WriteLine($"{layout.Name}: {plan.Count} parts ({string.Join(", ", plan.GroupBy(p => p.Template).Select(g => $"{g.Count()} {g.Key}"))})");

            var baseName = Path.Combine(Path.GetDirectoryName(layoutPath), Path.GetFileNameWithoutExtension(layoutPath));
            if (args.Contains("--dry-run"))
            {
                var csv = new StringBuilder("template,id,x_mm,y_mm,direction,length_cells,height_mm\n");
                foreach (var p in plan)
                    csv.AppendLine($"{p.Template},{p.Id},{p.XMm:0.#},{p.YMm:0.#},{p.Direction},{p.LengthCells},{p.HeightMm}");
                File.WriteAllText(baseName + ".plan.csv", csv.ToString());
                Console.WriteLine("Plan written to " + baseName + ".plan.csv");
                return 0;
            }

            var templates = args.Contains("--boxes") ? null
                : Path.GetFullPath(Opt("--templates") ?? Path.Combine(Path.GetDirectoryName(layoutPath), "templates"));
            var outPath = Path.GetFullPath(Opt("--out") ?? baseName + ".CATPart");
            try
            {
                var builder = new Builder(layout, plan, templates, !args.Contains("--no-grid"));
                builder.Run(outPath);
                File.WriteAllText(baseName + ".build_report.json", builder.Report(layoutPath, outPath));
                Console.WriteLine("Report written to " + baseName + ".build_report.json");
                return builder.Failures == 0 ? 0 : 3;
            }
            catch (Exception e) when (e is COMException || e is InvalidOperationException)
            {
                Console.WriteLine("Cannot build in CATIA: " + e.Message);
                return 4;
            }
        }
    }

    internal sealed class Builder
    {
        readonly Layout _layout;
        readonly List<CadInstance> _plan;
        readonly string _templates;
        readonly bool _grid;
        readonly Dictionary<string, int> _byMode = new Dictionary<string, int>();
        readonly List<string> _errors = new List<string>();
        readonly Stopwatch _total = new Stopwatch();
        double _tInputs, _tParts, _tSave;

        public int Failures => _errors.Count;

        public Builder(Layout layout, List<CadInstance> plan, string templatesDir, bool grid)
        {
            _layout = layout; _plan = plan; _templates = templatesDir; _grid = grid;
        }

        static dynamic GetCatia()
        {
            try { return Marshal.GetActiveObject("CATIA.Application"); }
            catch (COMException)
            {
                var type = Type.GetTypeFromProgID("CATIA.Application")
                           ?? throw new InvalidOperationException("CATIA V5 is not installed on this computer.");
                Console.WriteLine("Starting CATIA...");
                return Activator.CreateInstance(type);
            }
        }

        public void Run(string outPath)
        {
            _total.Start();
            dynamic catia = GetCatia();
            catia.Visible = true;
            catia.RefreshDisplay = false;
            try
            {
                dynamic partDoc = catia.Documents.Add("Part");
                dynamic part = partDoc.Part;
                dynamic hsf = part.HybridShapeFactory;
                dynamic planeXY = part.OriginElements.PlaneXY;
                double cell = _layout.CellMm;

                // 1. Floor slab and grid.
                var t = Stopwatch.StartNew();
                dynamic floorBody = NewBody(part, partDoc, "Floor", 150, 155, 160);
                Block(part, floorBody, planeXY, 0, 0, _layout.Width * cell, _layout.Height * cell, 20, reversed: true);
                if (_grid)
                {
                    dynamic grid = part.HybridBodies.Add();
                    grid.Name = "Floor grid";
                    for (int x = 0; x <= _layout.Width; x++) Line(part, hsf, grid, x * cell, 0, x * cell, _layout.Height * cell);
                    for (int y = 0; y <= _layout.Height; y++) Line(part, hsf, grid, 0, y * cell, _layout.Width * cell, y * cell);
                }

                // 2. Inputs: an origin point and a direction line per part (the PowerCopy inputs).
                dynamic inputs = part.HybridBodies.Add();
                inputs.Name = "Layout inputs";
                var origin = new Dictionary<CadInstance, dynamic>();
                var direction = new Dictionary<CadInstance, dynamic>();
                foreach (var p in _plan)
                {
                    dynamic pt = hsf.AddNewPointCoord(p.XMm, p.YMm, 0.0);
                    inputs.AppendHybridShape(pt);
                    pt.Name = p.Id + "_Origin";
                    dynamic tip = hsf.AddNewPointCoord(p.XMm + p.Direction.Dx() * cell / 2, p.YMm + p.Direction.Dy() * cell / 2, 0.0);
                    inputs.AppendHybridShape(tip);
                    tip.Name = p.Id + "_Tip";
                    dynamic line = hsf.AddNewLinePtPt(part.CreateReferenceFromObject(pt), part.CreateReferenceFromObject(tip));
                    inputs.AppendHybridShape(line);
                    line.Name = p.Id + "_Direction";
                    origin[p] = pt; direction[p] = line;
                }
                part.Update();
                _tInputs = t.Elapsed.TotalSeconds;

                // 3. One instantiation per part, or a block when there is no template.
                t.Restart();
                var blockBodies = new Dictionary<string, dynamic>();
                foreach (var p in _plan)
                {
                    var file = _templates == null ? null : Path.Combine(_templates, p.Template + ".CATPart");
                    try
                    {
                        if (file != null && File.Exists(file))
                        {
                            dynamic factory = part.GetCustomerFactory("InstanceFactory");
                            factory.BeginInstanceFactory("PC_" + p.Template, file);
                            factory.BeginInstantiate();
                            factory.PutInputData("Origin", origin[p]);
                            factory.PutInputData("Direction", direction[p]);
                            factory.PutInputData("Floor", planeXY);
                            factory.Instantiate();
                            factory.EndInstantiate();
                            factory.EndInstanceFactory();
                            Count("powercopy");
                        }
                        else
                        {
                            if (!blockBodies.TryGetValue(p.Template, out var body))
                            {
                                var (r, g, b) = Colour(p.Template);
                                blockBodies[p.Template] = body = NewBody(part, partDoc, p.Template, r, g, b);
                            }
                            BlockFor(part, body, planeXY, p, cell);
                            Count("block");
                        }
                    }
                    catch (Exception e) when (e is COMException || e is InvalidOperationException)
                    {
                        _errors.Add($"{p.Id} ({p.Template}): {e.Message}");
                        Console.WriteLine($"  failed: {p.Id} ({p.Template}): {e.Message}");
                    }
                }
                part.Update();
                _tParts = t.Elapsed.TotalSeconds;

                t.Restart();
                Directory.CreateDirectory(Path.GetDirectoryName(outPath));
                partDoc.SaveAs(outPath);
                _tSave = t.Elapsed.TotalSeconds;
                Console.WriteLine($"Saved {outPath}");
            }
            finally
            {
                catia.RefreshDisplay = true;
                _total.Stop();
            }
            Console.WriteLine($"Built {_plan.Count} parts in {_total.Elapsed.TotalSeconds:0.0} s " +
                              $"(inputs {_tInputs:0.0} s, parts {_tParts:0.0} s, save {_tSave:0.0} s), {Failures} failed.");
        }

        void Count(string mode) => _byMode[mode] = _byMode.TryGetValue(mode, out var n) ? n + 1 : 1;

        static (int, int, int) Colour(string template)
        {
            switch (template)
            {
                case "Robot": return (242, 128, 33);
                case "Drill": return (61, 110, 158);
                case "XRay": return (220, 224, 228);
                case "Pillar": return (158, 160, 163);
                case "Start": return (59, 171, 92);
                case "Stop": return (204, 51, 46);
                default: return (60, 63, 68); // conveyor
            }
        }

        static dynamic NewBody(dynamic part, dynamic partDoc, string name, int r, int g, int b)
        {
            dynamic body = part.Bodies.Add();
            body.Name = name;
            dynamic sel = partDoc.Selection;
            sel.Clear();
            sel.Add(body);
            sel.VisProperties.SetRealColor(r, g, b, 0);
            sel.Clear();
            return body;
        }

        static void Line(dynamic part, dynamic hsf, dynamic set, double x1, double y1, double x2, double y2)
        {
            dynamic a = hsf.AddNewPointCoord(x1, y1, 0.0);
            dynamic b = hsf.AddNewPointCoord(x2, y2, 0.0);
            dynamic l = hsf.AddNewLinePtPt(part.CreateReferenceFromObject(a), part.CreateReferenceFromObject(b));
            set.AppendHybridShape(l);
        }

        /// <summary>The block that stands in for a template: the part's footprint, padded to its height.</summary>
        static void BlockFor(dynamic part, dynamic body, dynamic planeXY, CadInstance p, double cell)
        {
            bool conveyor = p.Template.StartsWith("Conveyor");
            double width = conveyor && p.Template == CadPlan.ConveyorStraight ? 0.72 * cell
                : p.Template == "Robot" ? 0.6 * cell : p.Template == "Pillar" ? 0.8 * cell : cell;
            double back = -cell / 2, front = (p.LengthCells - 0.5) * cell;
            if (p.Template == "Robot" || p.Template == "Pillar") { back = -width / 2; front = width / 2; }
            double fx = p.Direction.Dx(), fy = p.Direction.Dy(), rx = fy, ry = -fx;
            var xs = new[] { back, front }.SelectMany(a => new[] { -width / 2, width / 2 }.Select(b => p.XMm + fx * a + rx * b)).ToArray();
            var ys = new[] { back, front }.SelectMany(a => new[] { -width / 2, width / 2 }.Select(b => p.YMm + fy * a + ry * b)).ToArray();
            Block(part, body, planeXY, xs.Min(), ys.Min(), xs.Max(), ys.Max(), p.HeightMm, reversed: false);
        }

        /// <summary>An axis-aligned rectangle sketched on the floor plane and padded.</summary>
        static void Block(dynamic part, dynamic body, dynamic planeXY, double x0, double y0, double x1, double y1, double height, bool reversed)
        {
            part.InWorkObject = body;
            dynamic sketch = body.Sketches.Add(planeXY);
            dynamic f2d = sketch.OpenEdition();
            var corners = new[] { (x0, y0), (x1, y0), (x1, y1), (x0, y1) };
            var pts = corners.Select(c => (dynamic)f2d.CreatePoint(c.Item1, c.Item2)).ToArray();
            for (int i = 0; i < 4; i++)
            {
                var a = corners[i]; var b = corners[(i + 1) % 4];
                dynamic l = f2d.CreateLine(a.Item1, a.Item2, b.Item1, b.Item2);
                l.StartPoint = pts[i];
                l.EndPoint = pts[(i + 1) % 4];
            }
            sketch.CloseEdition();
            part.InWorkObject = body;
            dynamic pad = part.ShapeFactory.AddNewPad(sketch, height);
            if (reversed) pad.DirectionOrientation = 1; // catInverseOrientation: the slab goes down from z = 0
        }

        public string Report(string layoutPath, string outPath)
        {
            string Q(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            var sb = new StringBuilder("{\n");
            sb.Append($"  \"layout\": {Q(layoutPath)},\n  \"catpart\": {Q(outPath)},\n");
            sb.Append($"  \"parts\": {_plan.Count},\n");
            sb.Append($"  \"byTemplate\": {{ {string.Join(", ", _plan.GroupBy(p => p.Template).Select(g => $"{Q(g.Key)}: {g.Count()}"))} }},\n");
            sb.Append($"  \"byMode\": {{ {string.Join(", ", _byMode.Select(kv => $"{Q(kv.Key)}: {kv.Value}"))} }},\n");
            sb.Append($"  \"seconds\": {{ \"total\": {_total.Elapsed.TotalSeconds:0.00}, \"inputs\": {_tInputs:0.00}, \"parts\": {_tParts:0.00}, \"save\": {_tSave:0.00} }},\n");
            sb.Append($"  \"failures\": [{string.Join(", ", _errors.Select(Q))}],\n");
            sb.Append($"  \"machine\": {Q(Environment.MachineName)},\n  \"when\": {Q(DateTime.Now.ToString("s"))}\n}}\n");
            return sb.ToString();
        }
    }
}
