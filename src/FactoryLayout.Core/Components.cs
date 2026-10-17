using System;
using System.Collections.Generic;
using System.Linq;

namespace FactoryLayout.Core
{
    public enum ComponentKind { Start, Stop, Robot, Drill, XRay, Pillar, Machine }

    /// <summary>How the conveyor has to serve a component.</summary>
    public enum ServiceRule
    {
        /// <summary>Not a station: an obstacle the conveyor goes around (a pillar).</summary>
        None,
        /// <summary>The conveyor begins (Start) or ends (Stop) in this cell.</summary>
        Endpoint,
        /// <summary>The conveyor must pass through the work envelope (robot arm).</summary>
        Envelope,
        /// <summary>
        /// The machine works on the cell it faces, over the belt: the conveyor must run straight
        /// through that cell, across the machine's face (drill).
        /// </summary>
        Front,
        /// <summary>The conveyor runs straight through the machine along its axis (X-ray tunnel).</summary>
        PassThrough,
        /// <summary>
        /// An enclosed machine whose loading side faces the conveyor: the belt must pass at least
        /// one cell along that side (milling or turning cell).
        /// </summary>
        Side,
    }

    /// <summary>
    /// What a component type is, independent of where it stands. The engine's equivalent of a
    /// PowerCopy template: the definition is fixed, each placement only supplies an origin and a
    /// direction (and, for a custom machine, its size).
    /// </summary>
    public sealed class ComponentSpec
    {
        public ComponentKind Kind { get; }
        public string Name { get; }
        /// <summary>Default body cells along the facing direction.</summary>
        public int Length { get; }
        /// <summary>Default body cells across the facing direction (to the right of it).</summary>
        public int Width { get; }
        /// <summary>
        /// Work envelope, in cells around the body (Chebyshev). A robot arm stands on one cell but
        /// sweeps the 3×3 around it, so its reach is 1. Nothing else may stand inside.
        /// </summary>
        public int Reach { get; }
        public ServiceRule Service { get; }
        /// <summary>Nominal height for 3D and CAD output.</summary>
        public int HeightMm { get; }
        public string Description { get; }
        /// <summary>Whether each placement may choose its own size.</summary>
        public bool Resizable { get; }

        public ComponentSpec(ComponentKind kind, string name, int length, int width, int reach,
            ServiceRule service, int heightMm, string description, bool resizable = false)
        {
            Kind = kind; Name = name; Length = length; Width = width; Reach = reach;
            Service = service; HeightMm = heightMm; Description = description; Resizable = resizable;
        }

        public bool IsStation => Service == ServiceRule.Envelope || Service == ServiceRule.Front
                                 || Service == ServiceRule.PassThrough || Service == ServiceRule.Side;
    }

    public static class Catalog
    {
        public const int MaxMachineCells = 8;

        static readonly Dictionary<ComponentKind, ComponentSpec> Specs = new Dictionary<ComponentKind, ComponentSpec>
        {
            [ComponentKind.Start] = new ComponentSpec(ComponentKind.Start, "Start", 1, 1, 0, ServiceRule.Endpoint, 900,
                "Infeed. The conveyor begins here."),
            [ComponentKind.Stop] = new ComponentSpec(ComponentKind.Stop, "Stop", 1, 1, 0, ServiceRule.Endpoint, 900,
                "Outfeed. The conveyor ends here."),
            [ComponentKind.Robot] = new ComponentSpec(ComponentKind.Robot, "Robot arm", 1, 1, 1, ServiceRule.Envelope, 1800,
                "Stands on one cell but needs the 3×3 around it to move. The conveyor must pass inside that envelope; nothing else may stand there."),
            [ComponentKind.Drill] = new ComponentSpec(ComponentKind.Drill, "Drill", 1, 1, 0, ServiceRule.Front, 2000,
                "Drills into the product on the belt in front of it. That cell is its work cell: the conveyor runs straight through it and no robot arm may reach in."),
            [ComponentKind.XRay] = new ComponentSpec(ComponentKind.XRay, "X-ray", 2, 1, 0, ServiceRule.PassThrough, 1600,
                "Inspection tunnel. The conveyor runs straight through it, in either direction."),
            [ComponentKind.Pillar] = new ComponentSpec(ComponentKind.Pillar, "Pillar", 1, 1, 0, ServiceRule.None, 3000,
                "Building column. An obstacle."),
            [ComponentKind.Machine] = new ComponentSpec(ComponentKind.Machine, "Machine", 2, 2, 0, ServiceRule.Side, 2400,
                "Enclosed machining cell (mill, lathe, press) of any size. The conveyor runs past its loading side, where one product at a time is taken in.",
                resizable: true),
        };

        public static ComponentSpec Get(ComponentKind kind) => Specs[kind];
        public static IEnumerable<ComponentSpec> All => Specs.Values;

        public static string IdPrefix(ComponentKind kind)
        {
            switch (kind)
            {
                case ComponentKind.Start: return "IN";
                case ComponentKind.Stop: return "OUT";
                case ComponentKind.Robot: return "R";
                case ComponentKind.Drill: return "D";
                case ComponentKind.XRay: return "X";
                case ComponentKind.Machine: return "M";
                default: return "P";
            }
        }
    }

    /// <summary>One placed component: a template plus an origin cell and a facing.</summary>
    public sealed class Component
    {
        int? _length, _width;

        public string Id { get; set; }
        public ComponentKind Kind { get; set; }
        /// <summary>First body cell. The body extends from here along <see cref="Facing"/> and to its right.</summary>
        public Cell Origin { get; set; }
        public Dir Facing { get; set; }
        /// <summary>Free text shown on the drawing (a custom machine's name, e.g. "CNC mill").</summary>
        public string Label { get; set; }
        /// <summary>Display colour as #RRGGBB, for custom machines; null = default.</summary>
        public string Colour { get; set; }

        public Component(string id, ComponentKind kind, Cell origin, Dir facing = Dir.East)
        {
            Id = id; Kind = kind; Origin = origin; Facing = facing;
        }

        public ComponentSpec Spec => Catalog.Get(Kind);
        public bool IsStation => Spec.IsStation;

        /// <summary>Body cells along the facing. Only resizable components can change it.</summary>
        public int Length
        {
            get => _length ?? Spec.Length;
            set => _length = Spec.Resizable ? Math.Max(1, Math.Min(Catalog.MaxMachineCells, value)) : (int?)null;
        }

        /// <summary>Body cells across the facing.</summary>
        public int Width
        {
            get => _width ?? Spec.Width;
            set => _width = Spec.Resizable ? Math.Max(1, Math.Min(Catalog.MaxMachineCells, value)) : (int?)null;
        }

        public string DisplayName => string.IsNullOrEmpty(Label) ? Spec.Name : Label;

        public IEnumerable<Cell> BodyCells()
        {
            var across = Facing.RotateCw();
            for (int i = 0; i < Length; i++)
                for (int j = 0; j < Width; j++)
                    yield return Origin.Step(Facing, i).Step(across, j);
        }

        /// <summary>Cells within <see cref="ComponentSpec.Reach"/> of the body, excluding the body.</summary>
        public IEnumerable<Cell> EnvelopeCells()
        {
            int r = Spec.Reach;
            if (r <= 0) yield break;
            var body = new HashSet<Cell>(BodyCells());
            var seen = new HashSet<Cell>();
            foreach (var b in body)
                for (int dx = -r; dx <= r; dx++)
                    for (int dy = -r; dy <= r; dy++)
                    {
                        var c = new Cell(b.X + dx, b.Y + dy);
                        if (!body.Contains(c) && seen.Add(c)) yield return c;
                    }
        }

        /// <summary>The cells along the front face, just beyond the body (one per body column).</summary>
        public IEnumerable<Cell> FaceCells()
        {
            var across = Facing.RotateCw();
            for (int j = 0; j < Width; j++) yield return Origin.Step(Facing, Length).Step(across, j);
        }

        /// <summary>
        /// Cells the component needs clear of other equipment, though the conveyor may (and must)
        /// run through them: a robot's envelope, a drill's work cell, a machine's loading side.
        /// </summary>
        public IEnumerable<Cell> WorkCells()
        {
            switch (Spec.Service)
            {
                case ServiceRule.Envelope: return EnvelopeCells();
                case ServiceRule.Front:
                case ServiceRule.Side: return FaceCells();
                default: return Enumerable.Empty<Cell>();
            }
        }

        /// <summary>The cell just beyond the body, in the facing direction (first face cell).</summary>
        public Cell FrontCell => Origin.Step(Facing, Length);

        /// <summary>The cell just behind the origin.</summary>
        public Cell BackCell => Origin.Step(Facing.Opposite());

        /// <summary>Body cells in the order the conveyor meets them travelling in <paramref name="travel"/>.</summary>
        public List<Cell> BodyCellsAlong(Dir travel)
        {
            var cells = BodyCells().ToList();
            if (travel == Facing.Opposite()) cells.Reverse();
            return cells;
        }

        /// <summary>
        /// Rotates a quarter turn clockwise about the body's centre (not its origin), so a large
        /// machine turns in place instead of swinging around its corner.
        /// </summary>
        public void RotateInPlace()
        {
            var before = BodyCells().ToList();
            double cx = before.Average(c => c.X), cy = before.Average(c => c.Y);
            Facing = Facing.RotateCw();
            var after = BodyCells().ToList();
            int dx = (int)Math.Round(cx - after.Average(c => c.X)), dy = (int)Math.Round(cy - after.Average(c => c.Y));
            Origin = new Cell(Origin.X + dx, Origin.Y + dy);
        }

        public Component Clone()
        {
            var c = new Component(Id, Kind, Origin, Facing) { Label = Label, Colour = Colour };
            c._length = _length; c._width = _width;
            return c;
        }

        public override string ToString() =>
            $"{Id} {Kind} at {Origin} facing {Facing}" + (Spec.Resizable ? $" {Length}x{Width}" : "");
    }

    public enum ConveyorMode
    {
        /// <summary>The router draws the conveyor.</summary>
        Auto,
        /// <summary>The user draws it cell by cell; the engine only checks it.</summary>
        Manual,
    }

    public sealed class Layout
    {
        public const string FormatId = "factory-layout/1";

        public string Name { get; set; } = "untitled";
        public int Width { get; set; } = 12;
        public int Height { get; set; } = 8;
        /// <summary>Tile size. 500 mm, as in the course project.</summary>
        public int CellMm { get; set; } = 500;
        /// <summary>Placement order is also the process order the conveyor visits stations in.</summary>
        public List<Component> Components { get; } = new List<Component>();
        /// <summary>Cells inside the bounding rectangle that are not floor (an L-shaped hall, a shaft).</summary>
        public HashSet<Cell> Voids { get; } = new HashSet<Cell>();
        public ConveyorMode ConveyorMode { get; set; } = ConveyorMode.Auto;
        /// <summary>The user's conveyor, Start first, when <see cref="ConveyorMode"/> is Manual.</summary>
        public List<Cell> ManualPath { get; } = new List<Cell>();

        public Layout() { }

        public Layout(int width, int height, string name = "untitled")
        {
            Width = width; Height = height; Name = name;
        }

        /// <summary>Inside the bounding rectangle.</summary>
        public bool InBounds(Cell c) => c.X >= 0 && c.Y >= 0 && c.X < Width && c.Y < Height;

        /// <summary>A floor tile: inside the rectangle and not removed.</summary>
        public bool IsFloor(Cell c) => InBounds(c) && !Voids.Contains(c);

        public int FloorCellCount => Width * Height - Voids.Count(InBounds);

        public Component Find(string id) => Components.FirstOrDefault(c => c.Id == id);

        /// <summary>The component whose body covers <paramref name="cell"/>, or null.</summary>
        public Component BodyAt(Cell cell) => Components.FirstOrDefault(c => c.BodyCells().Contains(cell));

        public IEnumerable<Component> OfKind(ComponentKind kind) => Components.Where(c => c.Kind == kind);
        public IEnumerable<Component> Stations => Components.Where(c => c.IsStation);

        public string NextId(ComponentKind kind)
        {
            string p = Catalog.IdPrefix(kind);
            for (int n = 1; ; n++)
            {
                string id = (kind == ComponentKind.Start || kind == ComponentKind.Stop) && n == 1 ? p : p + n;
                if (Find(id) == null) return id;
            }
        }

        public Component Add(ComponentKind kind, int x, int y, Dir facing = Dir.East)
        {
            var c = new Component(NextId(kind), kind, new Cell(x, y), facing);
            Components.Add(c);
            return c;
        }

        public Layout Clone()
        {
            var l = new Layout(Width, Height, Name) { CellMm = CellMm, ConveyorMode = ConveyorMode };
            foreach (var c in Components) l.Components.Add(c.Clone());
            l.Voids.UnionWith(Voids);
            l.ManualPath.AddRange(ManualPath);
            return l;
        }
    }
}
