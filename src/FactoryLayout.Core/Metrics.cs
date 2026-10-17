using System.Linq;

namespace FactoryLayout.Core
{
    public sealed class LayoutMetrics
    {
        public int Components { get; set; }
        public int ConveyorModules { get; set; }
        public int Straights { get; set; }
        public int Lefts { get; set; }
        public int Rights { get; set; }
        public int Turns => Lefts + Rights;
        public double ConveyorLengthM { get; set; }
        /// <summary>Parts a CAD model needs: components plus conveyor modules (the belt inside a tunnel belongs to the tunnel).</summary>
        public int CadInstances { get; set; }
        public int FloorCells { get; set; }
        public int BodyCells { get; set; }
        public int EnvelopeCells { get; set; }
        /// <summary>Share of the floor taken by equipment, work envelopes and conveyor.</summary>
        public double FloorUse { get; set; }

        public static LayoutMetrics Of(Layout layout, RouteResult route)
        {
            var m = new LayoutMetrics
            {
                Components = layout.Components.Count,
                FloorCells = layout.FloorCellCount,
                BodyCells = layout.Components.SelectMany(c => c.BodyCells()).Distinct().Count(c => layout.IsFloor(c)),
                EnvelopeCells = layout.Components.SelectMany(c => c.WorkCells()).Distinct().Count(c => layout.IsFloor(c)),
            };
            if (route != null && (route.Success || route.Manual) && route.Modules.Count > 0)
            {
                var belt = route.Modules.Where(x => x.InsideStationId == null && x.Kind != ModuleKind.Start && x.Kind != ModuleKind.Stop).ToList();
                m.Straights = belt.Count(x => x.Kind == ModuleKind.Straight);
                m.Lefts = belt.Count(x => x.Kind == ModuleKind.Left);
                m.Rights = belt.Count(x => x.Kind == ModuleKind.Right);
                m.ConveyorModules = belt.Count;
                // Centre line, cell to cell, Start to Stop.
                m.ConveyorLengthM = (route.Cells.Count - 1) * layout.CellMm / 1000.0;
            }
            m.CadInstances = m.Components + m.ConveyorModules;
            var used = layout.Components.SelectMany(c => c.BodyCells().Concat(c.WorkCells()))
                .Concat(route != null && (route.Success || route.Manual) ? route.Cells : Enumerable.Empty<Cell>())
                .Where(layout.IsFloor).Distinct().Count();
            m.FloorUse = m.FloorCells == 0 ? 0 : (double)used / m.FloorCells;
            return m;
        }
    }
}
