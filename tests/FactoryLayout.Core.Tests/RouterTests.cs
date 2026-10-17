using System.Linq;
using FactoryLayout.Core;

namespace FactoryLayout.Core.Tests;

public class RouterTests
{
    [Fact]
    public void Empty_floor_gives_a_straight_conveyor()
    {
        var l = new Layout(8, 3);
        l.Add(ComponentKind.Start, 0, 1);
        l.Add(ComponentKind.Stop, 7, 1);
        var r = ConveyorRouter.Route(l);
        Assert.True(r.Success, r.Failure);
        Assert.Equal(8, r.Cells.Count);
        Assert.Equal(0, r.Turns);
        Assert.Empty(RouteChecker.Check(l, r.Cells));
    }

    [Fact]
    public void L_shape_has_exactly_one_turn_and_names_it_correctly()
    {
        // Start south-west, Stop north-east. Pillars force "east first, then north": a left turn.
        var l = new Layout(6, 6);
        l.Add(ComponentKind.Start, 0, 0);
        l.Add(ComponentKind.Stop, 5, 5);
        l.Add(ComponentKind.Pillar, 0, 1);
        l.Add(ComponentKind.Pillar, 4, 5);
        var r = ConveyorRouter.Route(l);
        Assert.True(r.Success, r.Failure);
        Assert.Equal(1, r.Turns);
        Assert.Equal(ModuleKind.Left, r.Modules.Single(m => m.IsTurn).Kind);
        Assert.Empty(RouteChecker.Check(l, r.Cells));
    }

    [Fact]
    public void Conveyor_passes_through_the_robot_envelope_not_the_robot()
    {
        var l = new Layout(10, 9);
        l.Add(ComponentKind.Start, 0, 0);
        l.Add(ComponentKind.Stop, 9, 0);
        var robot = l.Add(ComponentKind.Robot, 5, 6);
        var r = ConveyorRouter.Route(l);
        Assert.True(r.Success, r.Failure);
        Assert.DoesNotContain(robot.Origin, r.Cells);
        Assert.Contains(r.Cells, c => robot.EnvelopeCells().Contains(c));
        Assert.Empty(RouteChecker.Check(l, r.Cells, r.StationOrder));
    }

    [Fact]
    public void Conveyor_runs_straight_through_the_xray_tunnel()
    {
        var l = new Layout(10, 8);
        l.Add(ComponentKind.Start, 0, 0);
        l.Add(ComponentKind.Stop, 9, 0);
        var x = l.Add(ComponentKind.XRay, 4, 4, Dir.North); // tunnel along Y: (4,4), (4,5)
        var r = ConveyorRouter.Route(l);
        Assert.True(r.Success, r.Failure);
        Assert.All(x.BodyCells(), b => Assert.Contains(b, r.Cells));
        Assert.All(r.Modules.Where(m => m.InsideStationId == x.Id), m => Assert.Equal(ModuleKind.Straight, m.Kind));
        Assert.Empty(RouteChecker.Check(l, r.Cells, r.StationOrder));
    }

    [Fact]
    public void Drill_is_served_at_the_cell_it_faces()
    {
        var l = new Layout(8, 6);
        l.Add(ComponentKind.Start, 0, 0);
        l.Add(ComponentKind.Stop, 7, 0);
        var d = l.Add(ComponentKind.Drill, 4, 5, Dir.South);
        var r = ConveyorRouter.Route(l);
        Assert.True(r.Success, r.Failure);
        Assert.Contains(d.FrontCell, r.Cells);
    }

    [Fact]
    public void Stations_are_visited_in_placement_order()
    {
        var l = new Layout(14, 9);
        l.Add(ComponentKind.Start, 0, 4);
        l.Add(ComponentKind.Stop, 13, 4);
        l.Add(ComponentKind.Robot, 10, 2); // first in process order, though further away
        l.Add(ComponentKind.Robot, 3, 6);
        var r = ConveyorRouter.Route(l);
        Assert.True(r.Success, r.Failure);
        Assert.Equal(new[] { "R1", "R2" }, r.StationOrder);
        Assert.Empty(RouteChecker.Check(l, r.Cells, r.StationOrder));
    }

    [Fact]
    public void Optimising_the_order_is_never_more_expensive()
    {
        int compared = 0;
        for (int seed = 1; seed <= 25; seed++)
        {
            var l = LayoutGenerator.Random(seed, 16, 12, robots: 3, drills: 1, xrays: 1, pillars: 2);
            var given = ConveyorRouter.Route(l);
            var opt = ConveyorRouter.Route(l, new RouteOptions { OptimizeOrder = true });
            if (!given.Success) continue;
            compared++;
            Assert.True(opt.Success);
            Assert.True(opt.Cost <= given.Cost + 1e-9, $"seed {seed}: {opt.Cost} > {given.Cost}");
        }
        Assert.True(compared >= 8, $"only {compared} compared");
    }

    [Fact]
    public void Every_successful_route_on_random_layouts_passes_the_independent_check()
    {
        int routed = 0;
        for (int seed = 1; seed <= 300; seed++)
        {
            var l = LayoutGenerator.Random(seed, 14, 10, robots: 2, drills: 1, xrays: 1, pillars: 3);
            var r = ConveyorRouter.Route(l);
            if (!r.Success) continue;
            routed++;
            var problems = RouteChecker.Check(l, r.Cells, r.StationOrder);
            Assert.True(problems.Count == 0, $"seed {seed}: {string.Join("; ", problems)}");
            Assert.Equal(r.Cells.Count, r.Modules.Count);
        }
        Assert.True(routed >= 140, $"only {routed}/300 routed");
    }

    [Fact]
    public void Layout_with_errors_is_not_routed()
    {
        var l = new Layout(6, 6);
        l.Add(ComponentKind.Start, 0, 0);
        var r = ConveyorRouter.Route(l);
        Assert.False(r.Success);
        Assert.Contains("Stop", r.Failure);
    }
}
