using System;
using System.Collections.Generic;

namespace FactoryLayout.Core
{
    /// <summary>Grid directions. North is +Y, East is +X (seen from above).</summary>
    public enum Dir { North = 0, East = 1, South = 2, West = 3 }

    public static class Dirs
    {
        public static readonly Dir[] All = { Dir.North, Dir.East, Dir.South, Dir.West };

        public static int Dx(this Dir d) => d == Dir.East ? 1 : d == Dir.West ? -1 : 0;
        public static int Dy(this Dir d) => d == Dir.North ? 1 : d == Dir.South ? -1 : 0;

        /// <summary>Clockwise seen from above: North to East is a right turn.</summary>
        public static Dir RotateCw(this Dir d) => (Dir)(((int)d + 1) & 3);
        public static Dir RotateCcw(this Dir d) => (Dir)(((int)d + 3) & 3);
        public static Dir Opposite(this Dir d) => (Dir)(((int)d + 2) & 3);

        /// <summary>Rotation about the vertical axis, in degrees counter-clockwise from East (+X).</summary>
        public static double AngleDeg(this Dir d) => d == Dir.East ? 0 : d == Dir.North ? 90 : d == Dir.West ? 180 : 270;

        /// <summary>Direction from a to an adjacent cell b.</summary>
        public static Dir Between(Cell a, Cell b)
        {
            int dx = b.X - a.X, dy = b.Y - a.Y;
            if (dx == 1 && dy == 0) return Dir.East;
            if (dx == -1 && dy == 0) return Dir.West;
            if (dx == 0 && dy == 1) return Dir.North;
            if (dx == 0 && dy == -1) return Dir.South;
            throw new ArgumentException($"{a} and {b} are not 4-adjacent");
        }

        public static bool TryParse(string s, out Dir d) => Enum.TryParse(s, true, out d);
    }

    /// <summary>One floor tile. (0,0) is the south-west corner.</summary>
    public readonly struct Cell : IEquatable<Cell>
    {
        public readonly int X;
        public readonly int Y;

        public Cell(int x, int y) { X = x; Y = y; }

        public Cell Step(Dir d, int n = 1) => new Cell(X + d.Dx() * n, Y + d.Dy() * n);

        public static int Chebyshev(Cell a, Cell b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
        public static int Manhattan(Cell a, Cell b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);

        public bool Equals(Cell o) => X == o.X && Y == o.Y;
        public override bool Equals(object obj) => obj is Cell c && Equals(c);
        public override int GetHashCode() => unchecked((X * 73856093) ^ (Y * 19349663));
        public static bool operator ==(Cell a, Cell b) => a.Equals(b);
        public static bool operator !=(Cell a, Cell b) => !a.Equals(b);
        public override string ToString() => $"({X},{Y})";
    }

    internal static class Seq
    {
        public static IEnumerable<IList<T>> Permutations<T>(IList<T> items)
        {
            var a = new List<T>(items);
            var c = new int[a.Count];
            yield return new List<T>(a);
            int i = 0;
            while (i < a.Count)
            {
                if (c[i] < i)
                {
                    int j = i % 2 == 0 ? 0 : c[i];
                    (a[j], a[i]) = (a[i], a[j]);
                    yield return new List<T>(a);
                    c[i]++;
                    i = 0;
                }
                else { c[i] = 0; i++; }
            }
        }
    }
}
