using System.Linq;
using FactoryLayout.Core;

namespace FactoryLayout.Core.Tests;

public class LayoutTests
{
    static Layout Line(int w = 10, int h = 7)
    {
        var l = new Layout(w, h, "line");
        l.Add(ComponentKind.Start, 0, 3);
        l.Add(ComponentKind.Stop, w - 1, 3);
        return l;
    }

    [Fact]
    public void Robot_needs_the_3x3_around_it()
    {
        var r = new Component("R1", ComponentKind.Robot, new Cell(4, 4));
        var env = r.EnvelopeCells().ToList();
        Assert.Equal(8, env.Count);
        Assert.All(env, c => Assert.Equal(1, Cell.Chebyshev(c, r.Origin)));
    }

    [Fact]
    public void Robot_against_the_wall_is_an_error()
    {
        var l = Line();
        l.Add(ComponentKind.Robot, 0, 0);
        Assert.Contains(LayoutValidator.Validate(l), i => i.Code == "E_ENVELOPE_OUTSIDE");
    }

    [Fact]
    public void Equipment_inside_a_work_envelope_is_an_error()
    {
        var l = Line();
        l.Add(ComponentKind.Robot, 4, 5);
        l.Add(ComponentKind.Pillar, 5, 6);
        Assert.Contains(LayoutValidator.Validate(l), i => i.Code == "E_ENVELOPE_BLOCKED");
    }

    [Fact]
    public void Shared_envelopes_are_a_warning_not_an_error()
    {
        var l = Line(12, 8);
        l.Add(ComponentKind.Robot, 4, 5);
        l.Add(ComponentKind.Robot, 6, 5);
        var issues = LayoutValidator.Validate(l);
        Assert.Contains(issues, i => i.Code == "W_ENVELOPES_OVERLAP");
        Assert.False(LayoutValidator.HasErrors(issues));
    }

    [Fact]
    public void Overlapping_bodies_are_an_error()
    {
        var l = Line();
        l.Add(ComponentKind.Pillar, 3, 1);
        l.Add(ComponentKind.Pillar, 3, 1);
        Assert.Contains(LayoutValidator.Validate(l), i => i.Code == "E_OVERLAP");
    }

    [Fact]
    public void Drill_facing_the_wall_is_an_error()
    {
        var l = Line();
        l.Add(ComponentKind.Drill, 5, 0, Dir.South);
        Assert.Contains(LayoutValidator.Validate(l), i => i.Code == "E_FRONT_BLOCKED");
    }

    [Fact]
    public void Missing_start_or_stop_is_an_error()
    {
        var l = new Layout(6, 6);
        var codes = LayoutValidator.Validate(l).Select(i => i.Code).ToList();
        Assert.Contains("E_START_COUNT", codes);
        Assert.Contains("E_STOP_COUNT", codes);
    }

    [Fact]
    public void Json_round_trip_keeps_every_component()
    {
        var l = Line();
        l.Name = "a \"quoted\" name";
        l.Add(ComponentKind.Robot, 4, 5, Dir.North);
        l.Add(ComponentKind.XRay, 6, 2, Dir.West);
        var back = LayoutJson.Read(LayoutJson.Write(l, ConveyorRouter.Route(l)));
        Assert.Equal(l.Name, back.Name);
        Assert.Equal(l.Width, back.Width);
        Assert.Equal(l.Components.Select(c => c.ToString()), back.Components.Select(c => c.ToString()));
    }
}
