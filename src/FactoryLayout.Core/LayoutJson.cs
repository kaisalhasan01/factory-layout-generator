using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace FactoryLayout.Core
{
    /// <summary>
    /// layout.json — the contract between the editor, the engine and the CAD builders. Components
    /// are the input; the conveyor and metrics are written for reference and are recomputed by
    /// whoever reads the file, so they can never disagree with the components.
    /// </summary>
    public static class LayoutJson
    {
        public static string Write(Layout layout, RouteResult route = null)
        {
            var w = new StringBuilder();
            w.Append("{\n");
            w.Append($"  \"format\": {Str(Layout.FormatId)},\n");
            w.Append($"  \"name\": {Str(layout.Name)},\n");
            w.Append($"  \"width\": {layout.Width},\n  \"height\": {layout.Height},\n  \"cellMm\": {layout.CellMm},\n");
            if (layout.Voids.Count > 0)
                w.Append($"  \"voids\": [{string.Join(", ", layout.Voids.OrderBy(v => v.Y).ThenBy(v => v.X).Select(v => $"[{v.X}, {v.Y}]"))}],\n");
            w.Append("  \"components\": [");
            for (int i = 0; i < layout.Components.Count; i++)
            {
                var c = layout.Components[i];
                w.Append(i == 0 ? "\n" : ",\n");
                w.Append($"    {{ \"id\": {Str(c.Id)}, \"kind\": {Str(c.Kind.ToString())}, \"x\": {c.Origin.X}, \"y\": {c.Origin.Y}, \"facing\": {Str(c.Facing.ToString())}");
                if (c.Spec.Resizable) w.Append($", \"length\": {c.Length}, \"width\": {c.Width}");
                if (!string.IsNullOrEmpty(c.Label)) w.Append($", \"label\": {Str(c.Label)}");
                if (!string.IsNullOrEmpty(c.Colour)) w.Append($", \"colour\": {Str(c.Colour)}");
                w.Append(" }");
            }
            w.Append(layout.Components.Count > 0 ? "\n  ]" : "]");
            w.Append($",\n  \"conveyorMode\": {Str(layout.ConveyorMode.ToString())}");
            if (layout.ConveyorMode == ConveyorMode.Manual)
                w.Append($",\n  \"path\": [{string.Join(", ", layout.ManualPath.Select(v => $"[{v.X}, {v.Y}]"))}]");

            if (route != null && route.Success && route.Modules.Count > 0)
            {
                var m = LayoutMetrics.Of(layout, route);
                w.Append(",\n  \"conveyor\": {\n");
                w.Append($"    \"stationOrder\": [{string.Join(", ", route.StationOrder.Select(Str))}],\n");
                w.Append("    \"modules\": [");
                for (int i = 0; i < route.Modules.Count; i++)
                {
                    var mod = route.Modules[i];
                    w.Append(i == 0 ? "\n" : ",\n");
                    w.Append($"      {{ \"kind\": {Str(mod.Kind.ToString())}, \"x\": {mod.Cell.X}, \"y\": {mod.Cell.Y}, \"in\": {Str(mod.In.ToString())}, \"out\": {Str(mod.Out.ToString())}");
                    if (mod.InsideStationId != null) w.Append($", \"inside\": {Str(mod.InsideStationId)}");
                    w.Append(" }");
                }
                w.Append("\n    ]\n  },\n");
                w.Append("  \"metrics\": {");
                w.Append($" \"conveyorLengthM\": {Num(m.ConveyorLengthM)}, \"straights\": {m.Straights}, \"lefts\": {m.Lefts}, \"rights\": {m.Rights},");
                w.Append($" \"cadInstances\": {m.CadInstances}, \"floorUse\": {Num(Math.Round(m.FloorUse, 3))} }}");
            }
            w.Append("\n}\n");
            return w.ToString();
        }

        public static Layout Read(string json)
        {
            if (!(MiniJson.Parse(json) is Dictionary<string, object> root))
                throw new FormatException("layout.json must be a JSON object");
            var fmt = Get<string>(root, "format", Layout.FormatId);
            if (fmt != Layout.FormatId) throw new FormatException($"Unknown format '{fmt}', expected '{Layout.FormatId}'");

            var layout = new Layout
            {
                Name = Get(root, "name", "untitled"),
                Width = (int)Get(root, "width", 12.0),
                Height = (int)Get(root, "height", 8.0),
                CellMm = (int)Get(root, "cellMm", 500.0),
            };
            if (root.TryGetValue("components", out var comps) && comps is List<object> list)
            {
                foreach (var o in list)
                {
                    if (!(o is Dictionary<string, object> c)) throw new FormatException("component must be an object");
                    var kindStr = Get<string>(c, "kind", null) ?? throw new FormatException("component without kind");
                    if (!Enum.TryParse(kindStr, true, out ComponentKind kind)) throw new FormatException($"unknown kind '{kindStr}'");
                    var facingStr = Get(c, "facing", "East");
                    if (!Dirs.TryParse(facingStr, out var facing)) throw new FormatException($"unknown facing '{facingStr}'");
                    var id = Get<string>(c, "id", null) ?? layout.NextId(kind);
                    var comp = new Component(id, kind, new Cell((int)Get(c, "x", 0.0), (int)Get(c, "y", 0.0)), facing)
                    {
                        Label = Get<string>(c, "label", null),
                        Colour = Get<string>(c, "colour", null),
                    };
                    if (comp.Spec.Resizable)
                    {
                        comp.Length = (int)Get(c, "length", (double)comp.Spec.Length);
                        comp.Width = (int)Get(c, "width", (double)comp.Spec.Width);
                    }
                    layout.Components.Add(comp);
                }
            }
            foreach (var v in Cells(root, "voids")) layout.Voids.Add(v);
            if (Enum.TryParse(Get(root, "conveyorMode", "Auto"), true, out ConveyorMode mode)) layout.ConveyorMode = mode;
            layout.ManualPath.AddRange(Cells(root, "path"));
            return layout;
        }

        static IEnumerable<Cell> Cells(Dictionary<string, object> d, string key)
        {
            if (!d.TryGetValue(key, out var v) || !(v is List<object> list)) yield break;
            foreach (var o in list)
                if (o is List<object> xy && xy.Count == 2 && xy[0] is double x && xy[1] is double y)
                    yield return new Cell((int)x, (int)y);
                else throw new FormatException($"'{key}' must be a list of [x, y] pairs");
        }

        static T Get<T>(Dictionary<string, object> d, string key, T fallback) =>
            d.TryGetValue(key, out var v) && v is T t ? t : fallback;

        static string Num(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        static string Str(string s)
        {
            var b = new StringBuilder("\"");
            foreach (var ch in s ?? "")
            {
                switch (ch)
                {
                    case '"': b.Append("\\\""); break;
                    case '\\': b.Append("\\\\"); break;
                    case '\n': b.Append("\\n"); break;
                    case '\r': b.Append("\\r"); break;
                    case '\t': b.Append("\\t"); break;
                    default:
                        if (ch < 0x20) b.Append("\\u").Append(((int)ch).ToString("x4"));
                        else b.Append(ch);
                        break;
                }
            }
            return b.Append('"').ToString();
        }
    }

    /// <summary>A small JSON reader, so the engine has no dependencies (Unity, .NET Framework and .NET all load it).</summary>
    public static class MiniJson
    {
        public static object Parse(string s)
        {
            int i = 0;
            var v = Value(s, ref i);
            Ws(s, ref i);
            if (i != s.Length) throw new FormatException($"Unexpected '{s[i]}' at {i}");
            return v;
        }

        static void Ws(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        static object Value(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) throw new FormatException("Unexpected end of JSON");
            char c = s[i];
            if (c == '{')
            {
                var d = new Dictionary<string, object>();
                i++; Ws(s, ref i);
                if (s[i] == '}') { i++; return d; }
                while (true)
                {
                    Ws(s, ref i);
                    var k = String(s, ref i);
                    Ws(s, ref i);
                    if (s[i++] != ':') throw new FormatException($"Expected ':' at {i - 1}");
                    d[k] = Value(s, ref i);
                    Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; return d; }
                    throw new FormatException($"Expected ',' or '}}' at {i}");
                }
            }
            if (c == '[')
            {
                var l = new List<object>();
                i++; Ws(s, ref i);
                if (s[i] == ']') { i++; return l; }
                while (true)
                {
                    l.Add(Value(s, ref i));
                    Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; return l; }
                    throw new FormatException($"Expected ',' or ']' at {i}");
                }
            }
            if (c == '"') return String(s, ref i);
            if (Lit(s, ref i, "true")) return true;
            if (Lit(s, ref i, "false")) return false;
            if (Lit(s, ref i, "null")) return null;
            int st = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (st == i) throw new FormatException($"Unexpected '{c}' at {i}");
            return double.Parse(s.Substring(st, i - st), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        static bool Lit(string s, ref int i, string lit)
        {
            if (string.CompareOrdinal(s, i, lit, 0, lit.Length) != 0) return false;
            i += lit.Length;
            return true;
        }

        static string String(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException($"Expected string at {i}");
            i++;
            var b = new StringBuilder();
            while (s[i] != '"')
            {
                char c = s[i++];
                if (c != '\\') { b.Append(c); continue; }
                char e = s[i++];
                switch (e)
                {
                    case 'n': b.Append('\n'); break;
                    case 'r': b.Append('\r'); break;
                    case 't': b.Append('\t'); break;
                    case 'b': b.Append('\b'); break;
                    case 'f': b.Append('\f'); break;
                    case 'u': b.Append((char)Convert.ToInt32(s.Substring(i, 4), 16)); i += 4; break;
                    default: b.Append(e); break;
                }
            }
            i++;
            return b.ToString();
        }
    }
}
