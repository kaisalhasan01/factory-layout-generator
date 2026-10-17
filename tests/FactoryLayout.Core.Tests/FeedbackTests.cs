using System.Linq;
using FactoryLayout.Core;

namespace FactoryLayout.Core.Tests;

/// <summary>Rules added after Kais's first test run (2026-10-15).</summary>
public class FeedbackTests
{
    static Layout Line(int w = 12, int h = 8)
    {
        var l = new Layout(w, h, "t");
        l.Add(ComponentKind.Start, 0, 1);
        l.Add(ComponentKind.Stop, w - 1, 1);
        return l;
    }

    // ---------------------------------------------------------------- drill work cell

    [Fact]
    public void Robot_reaching_into_a_drills_work_cell_is_an_error()
    {
        var l = Line();
        l.Add(ComponentKind.Drill, 5, 5, Dir.South);   // works on (5,4)
        l.Add(ComponentKind.Robot, 6, 3);              // envelope covers (5,4)
        Assert.Contains(LayoutValidator.Validate(l), i => i.Code == "E_WORKZONE_CONFLICT");
    }

    [Fact]
    public void Conveyor_runs_straight_across_the_drills_face()
    {
        var l = Line(10, 7);
        var d = l.Add(ComponentKind.Drill, 5, 5, Dir.South);
        var r = ConveyorRouter.Route(l);
        Assert.True(r.Success, r.Failure);
        var m = r.Modules.Single(x => x.Cell == d.FrontCell);
        Assert.Equal(ModuleKind.Straight, m.Kind);
        Assert.True(m.In == Dir.East || m.In == Dir.West);
        Assert.Empty(RouteChecker.Check(l, r.Cells, r.StationOrder));
    }

    [Fact]
    public void Drill_against_the_wall_sideways_is_caught_before_routing()
    {
        var l = Line(10, 7);
        l.Add(ComponentKind.Drill, 9, 5, Dir.South);   // works on (9,4); east of it is the wall
        Assert.Contains(LayoutValidator.Validate(l), i => i.Code == "E_FRONT_PASSAGE");
    }

    [Fact]
    public void Checker_rejects_a_turn_under_the_drill()
    {
        var l = new Layout(6, 6);
        l.Add(ComponentKind.Start, 0, 2);
        l.Add(ComponentKind.Stop, 2, 5);
        l.Add(ComponentKind.Drill, 3, 2, Dir.West); // works on (2,2)
        var path = new[] { new Cell(0, 2), new Cell(1, 2), new Cell(2, 2), new Cell(2, 3), new Cell(2, 4), new Cell(2, 5) };
        Assert.Contains(RouteChecker.Check(l, path), p => p.Contains("straight past D1"));
    }

    // ---------------------------------------------------------------- floor shape

    [Fact]
    public void Conveyor_avoids_removed_floor_cells()
    {
        var l = Line(10, 5);
        for (int y = 0; y < 4; y++) l.Voids.Add(new Cell(5, y)); // a wall with a gap at the top
        var r = ConveyorRouter.Route(l);
        Assert.True(r.Success, r.Failure);
        Assert.DoesNotContain(r.Cells, l.Voids.Contains);
        Assert.Contains(new Cell(5, 4), r.Cells);
        Assert.Empty(RouteChecker.Check(l, r.Cells));
    }

    [Fact]
    public void Equipment_on_a_removed_cell_is_an_error()
    {
        var l = Line();
        l.Voids.Add(new Cell(6, 6));
        l.Add(ComponentKind.Pillar, 6, 6);
        Assert.Contains(LayoutValidator.Validate(l), i => i.Code == "E_OUT_OF_BOUNDS");
    }

    [Fact]
    public void Robot_next_to_a_removed_cell_counts_as_against_the_wall()
    {
        var l = Line();
        l.Voids.Add(new Cell(6, 6));
        l.Add(ComponentKind.Robot, 5, 5);
        Assert.Contains(LayoutValidator.Validate(l), i => i.Code == "E_ENVELOPE_OUTSIDE");
    }

    // ---------------------------------------------------------------- custom machine

    [Fact]
    public void Machine_has_the_size_it_is_given_and_a_loading_side()
    {
        var m = new Component("M1", ComponentKind.Machine, new Cell(2, 2), Dir.South) { Length = 3, Width = 2 };
        Assert.Equal(6, m.BodyCells().Count());
        Assert.Equal(2, m.FaceCells().Count());
        Assert.All(m.FaceCells(), f => Assert.DoesNotContain(f, m.BodyCells()));
        m.Length = 99;
        Assert.Equal(Catalog.MaxMachineCells, m.Length);
    }

    [Fact]
    public void Conveyor_passes_the_machines_loading_side()
    {
        var l = new Layout(14, 10);
        l.Add(ComponentKind.Start, 0, 1);
        l.Add(ComponentKind.Stop, 13, 1);
        var m = new Component("M1", ComponentKind.Machine, new Cell(5, 8), Dir.South) { Length = 3, Width = 3, Label = "CNC mill" };
        l.Components.Add(m);
        var r = ConveyorRouter.Route(l);
        Assert.True(r.Success, r.Failure);
        Assert.Contains(r.Cells, c => m.FaceCells().Contains(c));
        Assert.DoesNotContain(r.Cells, c => m.BodyCells().Contains(c));
        Assert.Empty(RouteChecker.Check(l, r.Cells, r.StationOrder));
    }

    [Fact]
    public void Rotating_a_machine_keeps_it_in_place()
    {
        var m = new Component("M1", ComponentKind.Machine, new Cell(5, 5), Dir.East) { Length = 4, Width = 2 };
        double cx = m.BodyCells().Average(c => c.X), cy = m.BodyCells().Average(c => c.Y);
        m.RotateInPlace();
        Assert.Equal(Dir.South, m.Facing);
        Assert.True(System.Math.Abs(m.BodyCells().Average(c => c.X) - cx) <= 0.5);
        Assert.True(System.Math.Abs(m.BodyCells().Average(c => c.Y) - cy) <= 0.5);
    }

    // ---------------------------------------------------------------- hand-drawn conveyor

    [Fact]
    public void Half_drawn_conveyor_says_what_is_missing()
    {
        var l = Line(10, 6);
        l.Add(ComponentKind.Robot, 5, 3);
        l.ConveyorMode = ConveyorMode.Manual;
        l.ManualPath.AddRange(new[] { new Cell(0, 1), new Cell(1, 1), new Cell(2, 1) });
        var r = ConveyorRouter.Resolve(l);
        Assert.False(r.Success);
        Assert.True(r.Manual);
        Assert.Contains(r.Problems, p => p.Contains("R1"));
        Assert.Equal(3, r.Modules.Count);
    }

    [Fact]
    public void Finished_hand_drawn_conveyor_is_accepted()
    {
        var l = Line(8, 4);
        l.ConveyorMode = ConveyorMode.Manual;
        for (int x = 0; x < 8; x++) l.ManualPath.Add(new Cell(x, 1));
        var r = ConveyorRouter.Resolve(l);
        Assert.True(r.Success, r.Failure);
        Assert.Equal(3.5, LayoutMetrics.Of(l, r).ConveyorLengthM);
    }

    [Fact]
    public void Hand_drawn_U_turn_is_flagged()
    {
        var l = Line(8, 4);
        var path = new[] { new Cell(0, 1), new Cell(1, 1), new Cell(2, 1), new Cell(1, 1) };
        Assert.Contains(RouteChecker.Check(l, path, partial: true), p => p.Contains("U-turn") || p.Contains("crosses"));
    }

    [Fact]
    public void Auto_completion_keeps_the_drawn_piece_and_serves_the_rest()
    {
        var l = Line(14, 9);
        var r1 = l.Add(ComponentKind.Robot, 4, 3);
        l.Add(ComponentKind.Robot, 9, 6);
        var drawn = new[] { new Cell(0, 1), new Cell(1, 1), new Cell(2, 1), new Cell(3, 1), new Cell(3, 2) }; // reaches R1's envelope
        var r = ConveyorRouter.Complete(l, drawn);
        Assert.True(r.Success, r.Failure);
        Assert.Equal(drawn, r.Cells.Take(drawn.Length));
        Assert.Equal(new[] { "R1", "R2" }, r.StationOrder);
        Assert.Empty(RouteChecker.Check(l, r.Cells, r.StationOrder));
    }

    [Fact]
    public void New_layout_features_survive_json()
    {
        var l = Line();
        l.Voids.Add(new Cell(11, 7));
        l.Components.Add(new Component("M1", ComponentKind.Machine, new Cell(5, 5), Dir.South) { Length = 2, Width = 3, Label = "Fräs \"A\"", Colour = "#3366aa" });
        l.ConveyorMode = ConveyorMode.Manual;
        l.ManualPath.AddRange(new[] { new Cell(0, 1), new Cell(1, 1) });
        var back = LayoutJson.Read(LayoutJson.Write(l));
        var m = back.Find("M1");
        Assert.Equal((2, 3, "Fräs \"A\"", "#3366aa"), (m.Length, m.Width, m.Label, m.Colour));
        Assert.Contains(new Cell(11, 7), back.Voids);
        Assert.Equal(ConveyorMode.Manual, back.ConveyorMode);
        Assert.Equal(l.ManualPath, back.ManualPath);
    }
}
