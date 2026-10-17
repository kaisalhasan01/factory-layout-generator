using System.Collections.Generic;
using System.Linq;
using FactoryLayout.Core;
using UnityEngine;
using Component = FactoryLayout.Core.Component;

/// <summary>What the editor wants drawn on top of the blueprint this frame.</summary>
public sealed class OverlayState
{
    public Component Ghost;
    public bool GhostValid = true;
    public Component Selected;
    /// <summary>A single highlighted cell (floor painting, conveyor drawing).</summary>
    public Cell? HoverCell;
    public Color HoverColour = Color.white;
    /// <summary>Cells of the problem the user clicked on; drawn pulsing.</summary>
    public List<Cell> Focus = new List<Cell>();
    public float Pulse;
    /// <summary>End of a conveyor being drawn by hand: drag from here to continue.</summary>
    public Cell? PathEnd;
}

/// <summary>
/// Everything visible: the blueprint layer, the 3D world, and the morph between them
/// (0 = blueprint, 1 = 3D). Rebuilt from the layout after every edit; the engine is the only
/// source of truth.
/// </summary>
public sealed class FactoryView : MonoBehaviour
{
    public static readonly Color Paper = new Color32(0x12, 0x3f, 0x6e, 255);
    public static readonly Color Ink = new Color32(0xe8, 0xf1, 0xff, 255);
    public static readonly Color Faint = new Color32(0x5f, 0x86, 0xb3, 255);
    public static readonly Color Warn = new Color32(0xff, 0xcf, 0x5a, 255);
    public static readonly Color Bad = new Color32(0xff, 0x6b, 0x6b, 255);
    public static readonly Color Void = new Color32(0x0a, 0x23, 0x40, 255);
    static readonly Color FloorGrey = new Color(0.55f, 0.57f, 0.58f);

    Layout _layout;
    RouteResult _route;
    float S => _layout.CellMm / 1000f;

    Material _lineMat, _markMat, _overlayMat, _floorMat, _domeMat;
    GameObject _blueprint, _marks, _overlay, _world, _floor;
    readonly List<(Transform t, float delay)> _grow = new List<(Transform, float)>();
    readonly List<Models.RobotRig> _robots = new List<Models.RobotRig>();
    readonly List<GameObject> _domes = new List<GameObject>();
    List<Vector3> _flowPath = new List<Vector3>();
    readonly List<(Transform t, float s)> _products = new List<(Transform, float)>();
    float _spawnTimer;
    bool _worldDirty = true;

    public float Morph { get; private set; }
    public bool ShowZones3D { get; set; } = true;
    public bool Animate { get; set; } = true;

    bool HasConveyor => _route != null && _route.Modules.Count > 1;

    void Awake()
    {
        var sprite = Shader.Find("Sprites/Default");
        _lineMat = new Material(sprite);
        _markMat = new Material(sprite);
        _overlayMat = new Material(sprite);
        _domeMat = new Material(sprite) { color = new Color(Models.RobotOrange.r, Models.RobotOrange.g, Models.RobotOrange.b, 0.07f) };
        _floorMat = new Material(Shader.Find("Standard")) { color = Paper };
        _floorMat.SetFloat("_Glossiness", 0.15f);
    }

    // ------------------------------------------------------------------ geometry helpers

    Vector2 C(Cell c) => new Vector2((c.X + 0.5f) * S, (c.Y + 0.5f) * S);
    static Vector2 D(Dir d) => new Vector2(d.Dx(), d.Dy());
    static Vector3 W3(Vector2 p, float y = 0) => new Vector3(p.x, y, p.y);
    static Quaternion Face(Dir d) => Quaternion.LookRotation(new Vector3(d.Dx(), 0, d.Dy()));
    Vector2 Min(Cell c) => new Vector2(c.X * S, c.Y * S);
    Vector2 Max(Cell c) => new Vector2((c.X + 1) * S, (c.Y + 1) * S);

    /// <summary>Floor point under a screen position, as a cell (may be outside the floor).</summary>
    public Cell CellAt(Vector3 world) => new Cell(Mathf.FloorToInt(world.x / S), Mathf.FloorToInt(world.z / S));

    public Vector3 CellCentre(Cell c) => W3(C(c));

    public static Color MachineColour(Component c)
    {
        if (!string.IsNullOrEmpty(c.Colour) && ColorUtility.TryParseHtmlString(c.Colour, out var col)) return col;
        return Models.MachineBlue;
    }

    /// <summary>The conveyor's centre line, start to end, with each 90° module as an arc.</summary>
    List<Vector2> CentreLine()
    {
        var pts = new List<Vector2>();
        if (!HasConveyor) return pts;
        float r = S / 2;
        foreach (var m in _route.Modules)
        {
            var c = C(m.Cell);
            if (!m.IsTurn) { pts.Add(c); continue; }
            var hin = D(m.In); var hout = D(m.Out);
            var corner = c - hin * r + hout * r;
            for (int i = 0; i <= 8; i++)
            {
                float a = i / 8f * Mathf.PI / 2;
                pts.Add(corner + r * (Mathf.Cos(a) * -hout + Mathf.Sin(a) * hin));
            }
        }
        return pts;
    }

    // ------------------------------------------------------------------ rebuild

    public void Rebuild(Layout layout, RouteResult route, List<Issue> issues)
    {
        _layout = layout;
        _route = route;
        BuildBlueprint(issues);
        // The 3D world is only built when it is about to be seen: rebuilding hundreds of parts
        // on every cell of a drag would make drawing stutter.
        _worldDirty = true;
        if (Morph > 0) BuildWorld();
        ApplyMorph(Morph);
    }

    void Replace(ref GameObject go, string name, Mesh mesh, Material mat)
    {
        if (go != null) { Destroy(go.GetComponent<MeshFilter>().sharedMesh); Destroy(go); }
        go = new GameObject(name);
        go.transform.SetParent(transform, false);
        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var mr = go.AddComponent<MeshRenderer>();
        mr.sharedMaterial = mat;
        mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        mr.receiveShadows = false;
    }

    void BuildBlueprint(List<Issue> issues)
    {
        int Wc = _layout.Width, Hc = _layout.Height;
        float s = S, fw = Wc * s, fh = Hc * s;
        var b = new Strokes(0.03f);

        for (int x = 0; x <= Wc; x++) b.Seg(new Vector2(x * s, 0), new Vector2(x * s, fh), x % 5 == 0 ? 0.022f : 0.01f, Faint);
        for (int y = 0; y <= Hc; y++) b.Seg(new Vector2(0, y * s), new Vector2(fw, y * s), y % 5 == 0 ? 0.022f : 0.01f, Faint);

        // Removed floor: dark, crossed, with a wall line where it meets the floor.
        foreach (var v in _layout.Voids.Where(_layout.InBounds))
        {
            b.FillRect(Min(v), Max(v), Void);
            b.Seg(Min(v), Max(v), 0.008f, Faint);
        }
        b.Rect(Vector2.zero, new Vector2(fw, fh), 0.045f, Ink);
        foreach (var v in _layout.Voids.Where(_layout.InBounds))
            foreach (var d in Dirs.All)
                if (_layout.IsFloor(v.Step(d)))
                {
                    var c = C(v) + D(d) * s / 2; var along = new Vector2(-d.Dy(), d.Dx()) * s / 2;
                    b.Seg(c - along, c + along, 0.045f, Ink);
                }

        // Work zones: robot envelopes, drill work cells, machine loading sides.
        foreach (var c in _layout.Components)
        {
            if (c.Spec.Reach > 0)
            {
                var cells = c.EnvelopeCells().Concat(c.BodyCells()).ToList();
                var min = new Vector2(cells.Min(p => p.X) * s, cells.Min(p => p.Y) * s);
                var max = new Vector2((cells.Max(p => p.X) + 1) * s, (cells.Max(p => p.Y) + 1) * s);
                b.Hatch(min, max, 0.11f, 0.012f, new Color(Warn.r, Warn.g, Warn.b, 0.5f));
                b.Rect(min, max, 0.02f, Warn, dashed: true);
                b.Circle(C(c.Origin), s * (c.Spec.Reach + 0.5f), 0.012f, Warn, 48, dashed: true);
            }
            else if (c.Spec.Service == ServiceRule.Front || c.Spec.Service == ServiceRule.Side)
                foreach (var e in c.WorkCells().Where(_layout.InBounds))
                {
                    b.Hatch(Min(e) + Vector2.one * 0.03f, Max(e) - Vector2.one * 0.03f, 0.09f, 0.012f, new Color(Warn.r, Warn.g, Warn.b, 0.5f));
                    b.Rect(Min(e) + Vector2.one * 0.03f, Max(e) - Vector2.one * 0.03f, 0.018f, Warn, dashed: true);
                }
        }

        var line = CentreLine();
        if (line.Count > 1)
        {
            var edge = _route.Success || !_route.Manual ? Ink : new Color(0.75f, 0.85f, 1f);
            b.Ribbon(line, s * 0.62f, edge);
            b.Ribbon(line, s * 0.62f - 0.05f, Paper);
            for (int i = 0; i + 1 < line.Count; i += 2) b.Seg(line[i], line[i + 1], 0.008f, Faint);
            foreach (var m in _route.Modules.Where(m => m.Kind == ModuleKind.Straight && m.InsideStationId == null))
            {
                var c = C(m.Cell); var f = D(m.Out); var l = new Vector2(-f.y, f.x); float k = s * 0.13f;
                b.Seg(c - f * k + l * k, c + f * k, 0.02f, Ink);
                b.Seg(c - f * k - l * k, c + f * k, 0.02f, Ink);
            }
        }

        foreach (var c in _layout.Components) Symbol(b, c, Ink, 1f);

        foreach (var i in issues)
            foreach (var cell in i.Cells.Where(_layout.InBounds))
                b.Rect(Min(cell) + Vector2.one * 0.04f, Max(cell) - Vector2.one * 0.04f, 0.035f, i.Severity == Severity.Error ? Bad : Warn);

        Replace(ref _blueprint, "Blueprint", b.ToMesh("blueprint"), _lineMat);
    }

    void Symbol(Strokes b, Component c, Color ink, float a)
    {
        float s = S;
        var body = c.BodyCells().ToList();
        var min = new Vector2(body.Min(p => p.X) * s + 0.06f, body.Min(p => p.Y) * s + 0.06f);
        var max = new Vector2((body.Max(p => p.X) + 1) * s - 0.06f, (body.Max(p => p.Y) + 1) * s - 0.06f);
        var ctr = C(c.Origin);
        var col = new Color(ink.r, ink.g, ink.b, ink.a * a);
        var paper = new Color(Paper.r, Paper.g, Paper.b, a);
        switch (c.Kind)
        {
            case ComponentKind.Pillar:
                b.FillRect(min, max, new Color(Faint.r, Faint.g, Faint.b, a));
                b.Rect(min, max, 0.02f, col);
                b.Seg(min, max, 0.012f, col); b.Seg(new Vector2(min.x, max.y), new Vector2(max.x, min.y), 0.012f, col);
                break;
            case ComponentKind.Robot:
                b.Disc(ctr, s * 0.32f, paper);
                b.Circle(ctr, s * 0.32f, 0.025f, col);
                b.Disc(ctr, s * 0.09f, col);
                b.Seg(ctr, ctr + D(c.Facing) * s * 0.42f, 0.035f, col);
                break;
            case ComponentKind.Drill:
                b.FillRect(min, max, paper);
                b.Rect(min, max, 0.025f, col);
                b.Circle(ctr, s * 0.16f, 0.015f, col, 24);
                b.Seg(ctr - new Vector2(s * 0.25f, 0), ctr + new Vector2(s * 0.25f, 0), 0.008f, col);
                b.Seg(ctr - new Vector2(0, s * 0.25f), ctr + new Vector2(0, s * 0.25f), 0.008f, col);
                b.Arrow(ctr + D(c.Facing) * s * 0.2f, ctr + D(c.Facing) * s * 0.72f, 0.018f, col);
                // The head reaches over the work cell.
                var w = C(c.FrontCell);
                b.Circle(w, s * 0.2f, 0.015f, col, 24);
                b.Disc(w, s * 0.05f, col);
                break;
            case ComponentKind.XRay:
                b.FillRect(min, max, new Color(Paper.r, Paper.g, Paper.b, 0.6f * a));
                b.Rect(min, max, 0.025f, col);
                b.Rect(min + Vector2.one * 0.05f, max - Vector2.one * 0.05f, 0.01f, col, dashed: true);
                break;
            case ComponentKind.Machine:
            {
                var mc = MachineColour(c);
                b.FillRect(min, max, new Color(mc.r * 0.45f, mc.g * 0.45f, mc.b * 0.45f, 0.9f * a));
                b.Rect(min, max, 0.035f, col);
                b.Rect(min + Vector2.one * 0.07f, max - Vector2.one * 0.07f, 0.01f, col);
                // Loading side: a heavy yellow line along the face.
                var face = c.FaceCells().ToList();
                var p0 = C(face[0]) - D(c.Facing) * s / 2; var p1 = C(face[face.Count - 1]) - D(c.Facing) * s / 2;
                var along = (p1 - p0).sqrMagnitude > 1e-6f ? (p1 - p0).normalized : new Vector2(-c.Facing.Dy(), c.Facing.Dx());
                b.Seg(p0 - along * s * 0.42f, p1 + along * s * 0.42f, 0.07f, new Color(Warn.r, Warn.g, Warn.b, a));
                break;
            }
            case ComponentKind.Start:
            case ComponentKind.Stop:
                b.FillRect(min, max, paper);
                b.Rect(min, max, 0.032f, col);
                if (c.Kind == ComponentKind.Start)
                    b.Arrow(ctr - new Vector2(s * 0.15f, 0), ctr + new Vector2(s * 0.12f, 0), 0.02f, col);
                else
                {
                    b.Seg(ctr - new Vector2(s * 0.15f, s * 0.15f), ctr + new Vector2(s * 0.15f, s * 0.15f), 0.025f, col);
                    b.Seg(ctr - new Vector2(s * 0.15f, -s * 0.15f), ctr + new Vector2(s * 0.15f, -s * 0.15f), 0.025f, col);
                }
                break;
        }
    }

    /// <summary>Placement preview, selection, hovered cell, focused problem, drawing handle.</summary>
    public void SetOverlay(OverlayState st)
    {
        if (_layout == null) return;
        var b = new Strokes(0.05f);
        if (st.Ghost != null)
        {
            var col = st.GhostValid ? Ink : Bad;
            foreach (var e in st.Ghost.WorkCells())
                b.FillRect(Min(e) + Vector2.one * 0.02f, Max(e) - Vector2.one * 0.02f, new Color(Warn.r, Warn.g, Warn.b, 0.2f));
            foreach (var e in st.Ghost.BodyCells())
                b.FillRect(Min(e) + Vector2.one * 0.02f, Max(e) - Vector2.one * 0.02f, new Color(col.r, col.g, col.b, 0.12f));
            Symbol(b, st.Ghost, col, 0.8f);
        }
        if (st.Selected != null && _layout.Components.Contains(st.Selected))
        {
            var body = st.Selected.BodyCells().ToList();
            b.Rect(new Vector2(body.Min(p => p.X) * S, body.Min(p => p.Y) * S) - Vector2.one * 0.02f,
                new Vector2((body.Max(p => p.X) + 1) * S, (body.Max(p => p.Y) + 1) * S) + Vector2.one * 0.02f, 0.06f, Warn);
        }
        if (st.HoverCell.HasValue)
            b.Rect(Min(st.HoverCell.Value) + Vector2.one * 0.03f, Max(st.HoverCell.Value) - Vector2.one * 0.03f, 0.05f, st.HoverColour);
        if (st.PathEnd.HasValue)
        {
            b.Disc(C(st.PathEnd.Value), S * 0.2f, new Color(Warn.r, Warn.g, Warn.b, 0.9f));
            b.Circle(C(st.PathEnd.Value), S * 0.3f, 0.025f, Warn);
        }
        if (st.Focus.Count > 0)
        {
            float g = 0.04f + 0.04f * st.Pulse;
            foreach (var cell in st.Focus.Where(_layout.InBounds))
            {
                b.FillRect(Min(cell), Max(cell), new Color(Bad.r, Bad.g, Bad.b, 0.25f + 0.2f * st.Pulse));
                b.Rect(Min(cell) - Vector2.one * g, Max(cell) + Vector2.one * g, 0.06f, Bad);
            }
        }
        Replace(ref _overlay, "Overlay", b.ToMesh("overlay"), _overlayMat);
        _overlay.SetActive(Morph < 0.05f);
    }

    void BuildWorld()
    {
        _worldDirty = false;
        if (_world != null) Destroy(_world);
        if (_floor != null) Destroy(_floor);
        _grow.Clear(); _robots.Clear(); _domes.Clear();
        foreach (var p in _products) if (p.t != null) Destroy(p.t.gameObject);
        _products.Clear();

        float s = S, fw = _layout.Width * s, fh = _layout.Height * s;

        // Floor slab, row by row, so removed cells are real holes.
        _floor = new GameObject("Floor");
        _floor.transform.SetParent(transform, false);
        for (int y = 0; y < _layout.Height; y++)
            for (int x = 0; x < _layout.Width;)
            {
                if (!_layout.IsFloor(new Cell(x, y))) { x++; continue; }
                int x0 = x;
                while (x < _layout.Width && _layout.IsFloor(new Cell(x, y))) x++;
                var slab = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Destroy(slab.GetComponent<Collider>());
                slab.transform.SetParent(_floor.transform, false);
                slab.transform.localPosition = new Vector3((x0 + x) * s / 2, -0.05f, (y + 0.5f) * s);
                slab.transform.localScale = new Vector3((x - x0) * s, 0.1f, s);
                slab.GetComponent<MeshRenderer>().sharedMaterial = _floorMat;
            }

        _world = new GameObject("World");
        _world.transform.SetParent(transform, false);
        float Delay(Cell c) => (c.X + 0.35f * c.Y) / (_layout.Width + 0.35f * _layout.Height);

        Transform Root(string name, Cell c, Quaternion rot)
        {
            var t = Models.Node(name, _world.transform, CellCentre(c), rot);
            _grow.Add((t, Delay(c)));
            return t;
        }

        // Floor markings: tile grid, safety zones.
        var mk = new Strokes(0.004f);
        var gridCol = new Color(0.25f, 0.27f, 0.29f, 0.45f);
        // Tile joints, only where there is floor on at least one side.
        for (int x = 0; x <= _layout.Width; x++)
            for (int y = 0; y < _layout.Height; y++)
                if (_layout.IsFloor(new Cell(x, y)) || _layout.IsFloor(new Cell(x - 1, y)))
                    mk.Seg(new Vector2(x * s, y * s), new Vector2(x * s, (y + 1) * s), 0.01f, gridCol);
        for (int y = 0; y <= _layout.Height; y++)
            for (int x = 0; x < _layout.Width; x++)
                if (_layout.IsFloor(new Cell(x, y)) || _layout.IsFloor(new Cell(x, y - 1)))
                    mk.Seg(new Vector2(x * s, y * s), new Vector2((x + 1) * s, y * s), 0.01f, gridCol);
        var safety = new Color(0.95f, 0.76f, 0.19f, 0.75f);

        Dir? startOut = null, stopIn = null;
        if (HasConveyor)
        {
            startOut = _route.Modules.First().Out;
            if (_route.Modules.Last().Kind == ModuleKind.Stop) stopIn = _route.Modules.Last().In;
            for (int i = 0; i < _route.Modules.Count; i++)
            {
                var m = _route.Modules[i];
                var dir = m.Kind == ModuleKind.Start ? m.Out : m.In;
                var t = Root($"Conveyor {i} {m.Kind}", m.Cell, Face(dir));
                if (m.IsTurn) Models.ConveyorTurn(t, s, m.Kind == ModuleKind.Left);
                else Models.ConveyorStraight(t, s);
            }
        }

        foreach (var c in _layout.Components)
        {
            switch (c.Kind)
            {
                case ComponentKind.Start: Models.StartStation(Root(c.Id, c.Origin, Face(startOut ?? c.Facing)), s); break;
                case ComponentKind.Stop: Models.StopStation(Root(c.Id, c.Origin, Face(stopIn ?? c.Facing)), s); break;
                case ComponentKind.Drill:
                    Models.Drill(Root(c.Id, c.Origin, Face(c.Facing)), s);
                    if (ShowZones3D) mk.Hatch(Min(c.FrontCell) + Vector2.one * 0.03f, Max(c.FrontCell) - Vector2.one * 0.03f, 0.14f, 0.03f, safety);
                    break;
                case ComponentKind.XRay: Models.XRay(Root(c.Id, c.Origin, Face(c.Facing)), s, c.Length); break;
                case ComponentKind.Pillar: Models.Pillar(Root(c.Id, c.Origin, Face(c.Facing)), s); break;
                case ComponentKind.Machine:
                    Models.Machine(Root(c.Id, c.Origin, Face(c.Facing)), s, c.Length, c.Width, MachineColour(c));
                    if (ShowZones3D)
                        foreach (var e in c.FaceCells().Where(_layout.IsFloor))
                            mk.Rect(Min(e) + Vector2.one * 0.03f, Max(e) - Vector2.one * 0.03f, 0.04f, safety);
                    break;
                case ComponentKind.Robot:
                {
                    var root = Root(c.Id, c.Origin, Face(c.Facing));
                    var rig = Models.Robot(root, s);
                    rig.Phase = (c.Origin.X * 1.7f + c.Origin.Y * 0.9f) % 6.28f;
                    // Point the arm at the conveyor where it passes through the envelope.
                    var env = new HashSet<Cell>(c.EnvelopeCells());
                    var served = HasConveyor ? _route.Cells.Where(env.Contains).ToList() : new List<Cell>();
                    if (served.Count > 0)
                    {
                        var avg = served.Aggregate(Vector2.zero, (acc, x) => acc + C(x)) / served.Count - C(c.Origin);
                        float worldYaw = Mathf.Atan2(avg.x, avg.y) * Mathf.Rad2Deg;
                        rig.BaseYaw = worldYaw - root.rotation.eulerAngles.y;
                    }
                    _robots.Add(rig);
                    Models.AnimateRobot(rig, 0);

                    if (ShowZones3D)
                    {
                        var dome = Models.Part(_world.transform, PrimitiveType.Sphere, CellCentre(c.Origin),
                            new Vector3(3 * s, 2.0f, 3 * s), Color.white);
                        dome.GetComponent<MeshRenderer>().sharedMaterial = _domeMat;
                        dome.GetComponent<MeshRenderer>().shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                        _domes.Add(dome.gameObject);

                        var cells = c.EnvelopeCells().Concat(c.BodyCells()).ToList();
                        var min = new Vector2(cells.Min(p => p.X) * s, cells.Min(p => p.Y) * s) + Vector2.one * 0.03f;
                        var max = new Vector2((cells.Max(p => p.X) + 1) * s, (cells.Max(p => p.Y) + 1) * s) - Vector2.one * 0.03f;
                        mk.Hatch(min, max, 0.16f, 0.035f, safety);
                        mk.Rect(min, max, 0.06f, new Color(0.95f, 0.76f, 0.19f, 0.95f));
                    }
                    break;
                }
            }
        }
        Replace(ref _marks, "Floor markings", mk.ToMesh("marks"), _markMat);

        _flowPath = _route != null && _route.Success ? CentreLine().Select(p => W3(p, Models.BeltTop + 0.09f)).ToList() : new List<Vector3>();
    }

    // ------------------------------------------------------------------ morph

    static float Smooth(float x) { x = Mathf.Clamp01(x); return x * x * (3 - 2 * x); }

    public void ApplyMorph(float t)
    {
        Morph = t;
        if (_blueprint == null) return;
        if (t > 0 && _worldDirty) BuildWorld();
        float bpAlpha = 1 - Smooth((t - 0.3f) / 0.45f);
        _lineMat.color = new Color(1, 1, 1, bpAlpha);
        _blueprint.SetActive(bpAlpha > 0.003f);
        if (_overlay != null) _overlay.SetActive(t < 0.05f);

        _floorMat.color = Color.Lerp(Paper, FloorGrey, Smooth((t - 0.1f) / 0.5f));
        float markA = Smooth((t - 0.45f) / 0.35f);
        _markMat.color = new Color(1, 1, 1, markA);
        if (_marks != null) _marks.SetActive(markA > 0.003f);

        foreach (var (tr, delay) in _grow)
        {
            float g = Smooth((t - 0.3f - 0.3f * delay) / 0.35f);
            tr.localScale = new Vector3(1, Mathf.Max(g, 0.0001f), 1);
            tr.gameObject.SetActive(g > 0.0005f);
        }
        float domeA = Smooth((t - 0.85f) / 0.15f);
        foreach (var d in _domes) d.SetActive(domeA > 0.01f);
        _domeMat.color = new Color(Models.RobotOrange.r, Models.RobotOrange.g, Models.RobotOrange.b, 0.08f * domeA);
        if (t < 0.999f) foreach (var p in _products) if (p.t != null) p.t.gameObject.SetActive(false);
    }

    void Update()
    {
        if (Morph < 0.999f || !Animate) return;
        foreach (var rig in _robots) Models.AnimateRobot(rig, Time.time);
        FlowProducts(Time.deltaTime);
    }

    /// <summary>Boxes ride the conveyor from Start to Stop, so the line reads as a line.</summary>
    void FlowProducts(float dt)
    {
        if (_flowPath.Count < 2) return;
        const float speed = 0.45f, every = 1.6f;
        _spawnTimer -= dt;
        if (_spawnTimer <= 0)
        {
            _spawnTimer = every;
            var box = Models.Part(_world.transform, PrimitiveType.Cube, _flowPath[0], Vector3.one * 0.18f, Models.Cardboard);
            _products.Add((box, 0f));
        }
        for (int i = _products.Count - 1; i >= 0; i--)
        {
            var (t, dist) = _products[i];
            if (t == null) { _products.RemoveAt(i); continue; }
            dist += speed * dt;
            if (!PointAt(dist, out var pos, out var dir)) { Destroy(t.gameObject); _products.RemoveAt(i); continue; }
            t.gameObject.SetActive(true);
            t.position = pos;
            if (dir.sqrMagnitude > 1e-6f) t.rotation = Quaternion.LookRotation(dir);
            _products[i] = (t, dist);
        }
    }

    bool PointAt(float dist, out Vector3 pos, out Vector3 dir)
    {
        for (int i = 0; i + 1 < _flowPath.Count; i++)
        {
            float len = Vector3.Distance(_flowPath[i], _flowPath[i + 1]);
            if (dist <= len)
            {
                pos = Vector3.Lerp(_flowPath[i], _flowPath[i + 1], len > 0 ? dist / len : 0);
                dir = _flowPath[i + 1] - _flowPath[i];
                return true;
            }
            dist -= len;
        }
        pos = default; dir = default;
        return false;
    }
}
