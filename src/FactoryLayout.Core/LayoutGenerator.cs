using System;
using System.Linq;

namespace FactoryLayout.Core
{
    /// <summary>Random valid layouts, for tests and benchmarks.</summary>
    public static class LayoutGenerator
    {
        public static Layout Random(int seed, int width, int height, int robots, int drills, int xrays, int pillars,
            int triesPerComponent = 400)
        {
            var rng = new Random(seed);
            var layout = new Layout(width, height, $"random-{width}x{height}-s{seed}");

            void Place(ComponentKind kind)
            {
                for (int t = 0; t < triesPerComponent; t++)
                {
                    var c = new Component(layout.NextId(kind), kind,
                        new Cell(rng.Next(width), rng.Next(height)), Dirs.All[rng.Next(4)]);
                    layout.Components.Add(c);
                    bool ok = !LayoutValidator.Validate(layout).Any(i => i.Severity == Severity.Error
                        && i.Code != "E_START_COUNT" && i.Code != "E_STOP_COUNT");
                    if (ok) return;
                    layout.Components.Remove(c);
                }
                throw new InvalidOperationException($"could not place a {kind} in {width}×{height} (seed {seed})");
            }

            Place(ComponentKind.Start);
            Place(ComponentKind.Stop);
            for (int i = 0; i < pillars; i++) Place(ComponentKind.Pillar);
            for (int i = 0; i < robots; i++) Place(ComponentKind.Robot);
            for (int i = 0; i < drills; i++) Place(ComponentKind.Drill);
            for (int i = 0; i < xrays; i++) Place(ComponentKind.XRay);

            // Process order: stations in a random order, the rest after.
            var stations = layout.Components.Where(c => c.IsStation).OrderBy(_ => rng.Next()).ToList();
            var others = layout.Components.Where(c => !c.IsStation).ToList();
            layout.Components.Clear();
            layout.Components.AddRange(others);
            layout.Components.AddRange(stations);
            return layout;
        }
    }
}
