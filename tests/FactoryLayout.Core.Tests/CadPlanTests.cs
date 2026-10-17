using System.IO;
using System.Linq;
using FactoryLayout.Core;

namespace FactoryLayout.Core.Tests;

public class CadPlanTests
{
    static (Layout, RouteResult) Demo()
    {
        var dir = Directory.GetCurrentDirectory();
        while (!File.Exists(Path.Combine(dir, "layouts", "demo-line.json"))) dir = Path.GetDirectoryName(dir);
        var l = LayoutJson.Read(File.ReadAllText(Path.Combine(dir, "layouts", "demo-line.json")));
        return (l, ConveyorRouter.Route(l));
    }

    [Fact]
    public void Plan_has_one_entry_per_CAD_instance()
    {
        var (l, r) = Demo();
        Assert.True(r.Success, r.Failure);
        var plan = CadPlan.From(l, r);
        Assert.Equal(LayoutMetrics.Of(l, r).CadInstances, plan.Count);
        Assert.Equal(l.Components.Count, plan.Count(p => !p.Template.StartsWith("Conveyor")));
    }

    [Fact]
    public void Turn_modules_become_left_and_right_templates()
    {
        var (l, r) = Demo();
        var plan = CadPlan.From(l, r);
        var m = LayoutMetrics.Of(l, r);
        Assert.Equal(m.Lefts, plan.Count(p => p.Template == CadPlan.ConveyorLeft));
        Assert.Equal(m.Rights, plan.Count(p => p.Template == CadPlan.ConveyorRight));
        Assert.Equal(m.Straights, plan.Count(p => p.Template == CadPlan.ConveyorStraight));
    }

    [Fact]
    public void Start_faces_where_the_belt_leaves_and_positions_are_cell_centres()
    {
        var (l, r) = Demo();
        var plan = CadPlan.From(l, r);
        var start = plan.Single(p => p.Template == "Start");
        Assert.Equal(r.Modules[0].Out, start.Direction);
        var s = l.OfKind(ComponentKind.Start).Single();
        Assert.Equal((s.Origin.X + 0.5) * l.CellMm, start.XMm);
        Assert.Equal((s.Origin.Y + 0.5) * l.CellMm, start.YMm);
    }

    [Fact]
    public void Unrouted_layout_still_lists_its_components()
    {
        var l = new Layout(6, 6);
        l.Add(ComponentKind.Start, 0, 0);
        l.Add(ComponentKind.Pillar, 3, 3);
        var plan = CadPlan.From(l, ConveyorRouter.Route(l));
        Assert.Equal(2, plan.Count);
    }
}
