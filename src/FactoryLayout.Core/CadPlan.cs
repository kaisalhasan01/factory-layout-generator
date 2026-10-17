using System.Collections.Generic;
using System.Linq;

namespace FactoryLayout.Core
{
    /// <summary>
    /// One part to place in CAD: which template, and the two inputs every template takes — an
    /// origin point on the floor and a direction. This is the PowerCopy contract: the CATIA
    /// builder turns each entry into a point, a line and one instantiation.
    /// </summary>
    public sealed class CadInstance
    {
        /// <summary>Template name; the CATIA builder looks for &lt;Template&gt;.CATPart with a PowerCopy "PC_&lt;Template&gt;".</summary>
        public string Template { get; set; }
        public string Id { get; set; }
        /// <summary>Centre of the origin cell, in millimetres from the floor's south-west corner.</summary>
        public double XMm { get; set; }
        public double YMm { get; set; }
        /// <summary>Facing / direction of travel as a unit grid vector.</summary>
        public Dir Direction { get; set; }
        public int LengthCells { get; set; } = 1;
        public int WidthCells { get; set; } = 1;
        /// <summary>A custom machine's name, for the CAD tree.</summary>
        public string Label { get; set; }
        public int HeightMm { get; set; }

        public override string ToString() => $"{Template} {Id} ({XMm:0},{YMm:0}) {Direction}";
    }

    public static class CadPlan
    {
        public const string ConveyorStraight = "ConveyorStraight";
        public const string ConveyorLeft = "ConveyorLeft";
        public const string ConveyorRight = "ConveyorRight";

        /// <summary>
        /// Every part the CAD model needs: the components, then the conveyor modules in flow
        /// order. The belt inside an X-ray tunnel belongs to the tunnel's template, and the Start
        /// and Stop templates include the belt on their own cell, so neither is listed twice.
        /// </summary>
        public static List<CadInstance> From(Layout layout, RouteResult route)
        {
            var list = new List<CadInstance>();
            double cell = layout.CellMm;
            CadInstance At(string template, string id, Cell c, Dir d, int len, int h) => new CadInstance
            {
                Template = template, Id = id, XMm = (c.X + 0.5) * cell, YMm = (c.Y + 0.5) * cell,
                Direction = d, LengthCells = len, HeightMm = h,
            };

            bool routed = route != null && route.Success && route.Modules.Count > 0;
            foreach (var c in layout.Components)
            {
                var dir = c.Facing;
                if (routed && c.Kind == ComponentKind.Start) dir = route.Modules[0].Out;
                if (routed && c.Kind == ComponentKind.Stop) dir = route.Modules[route.Modules.Count - 1].In;
                var inst = At(c.Kind.ToString(), c.Id, c.Origin, dir, c.Length, c.Spec.HeightMm);
                inst.WidthCells = c.Width;
                inst.Label = c.Label;
                list.Add(inst);
            }

            if (routed)
            {
                int n = 0;
                foreach (var m in route.Modules)
                {
                    if (m.InsideStationId != null || m.Kind == ModuleKind.Start || m.Kind == ModuleKind.Stop) continue;
                    string t = m.Kind == ModuleKind.Left ? ConveyorLeft : m.Kind == ModuleKind.Right ? ConveyorRight : ConveyorStraight;
                    list.Add(At(t, $"C{++n:D3}", m.Cell, m.In, 1, 800));
                }
            }
            return list;
        }

        public static IEnumerable<string> Templates(IEnumerable<CadInstance> plan) => plan.Select(p => p.Template).Distinct();
    }
}
