using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Flat line drawing on the floor plane, as triangles, so that blueprint strokes keep their width
/// at any zoom (GL lines are always one pixel). Coordinates are floor coordinates in metres:
/// x = world X, y = world Z.
/// </summary>
public sealed class Strokes
{
    readonly List<Vector3> _v = new List<Vector3>();
    readonly List<Color> _c = new List<Color>();
    readonly List<int> _t = new List<int>();
    readonly float _y;

    public Strokes(float height) { _y = height; }

    Vector3 P(Vector2 p) => new Vector3(p.x, _y, p.y);

    // Vertex colours are not gamma-corrected in linear colour space, so convert them here or
    // every stroke renders lighter than the same colour on a material or the camera background.
    static Color Lin(Color c) => QualitySettings.activeColorSpace == ColorSpace.Linear ? c.linear : c;

    public void Quad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, Color col)
    {
        col = Lin(col);
        int i = _v.Count;
        _v.Add(P(a)); _v.Add(P(b)); _v.Add(P(c)); _v.Add(P(d));
        _c.Add(col); _c.Add(col); _c.Add(col); _c.Add(col);
        _t.Add(i); _t.Add(i + 1); _t.Add(i + 2);
        _t.Add(i); _t.Add(i + 2); _t.Add(i + 3);
    }

    public void Seg(Vector2 a, Vector2 b, float w, Color col)
    {
        var d = b - a;
        if (d.sqrMagnitude < 1e-10f) return;
        d.Normalize();
        var n = new Vector2(-d.y, d.x) * (w * 0.5f);
        var e = d * (w * 0.5f); // square caps, so polylines join without gaps
        Quad(a - n - e, b - n + e, b + n + e, a + n - e, col);
    }

    public void Poly(IList<Vector2> pts, float w, Color col, bool closed = false)
    {
        for (int i = 0; i + 1 < pts.Count; i++) Seg(pts[i], pts[i + 1], w, col);
        if (closed && pts.Count > 2) Seg(pts[pts.Count - 1], pts[0], w, col);
    }

    public void Dashed(Vector2 a, Vector2 b, float w, float dash, float gap, Color col)
    {
        float len = Vector2.Distance(a, b);
        var d = (b - a) / Mathf.Max(len, 1e-6f);
        for (float s = 0; s < len; s += dash + gap)
            Seg(a + d * s, a + d * Mathf.Min(s + dash, len), w, col);
    }

    public void Rect(Vector2 min, Vector2 max, float w, Color col, bool dashed = false)
    {
        var c = new[] { min, new Vector2(max.x, min.y), max, new Vector2(min.x, max.y) };
        for (int i = 0; i < 4; i++)
        {
            if (dashed) Dashed(c[i], c[(i + 1) % 4], w, w * 4, w * 3, col);
            else Seg(c[i], c[(i + 1) % 4], w, col);
        }
    }

    public void FillRect(Vector2 min, Vector2 max, Color col) =>
        Quad(min, new Vector2(min.x, max.y), max, new Vector2(max.x, min.y), col);

    public void Circle(Vector2 c, float r, float w, Color col, int seg = 40, bool dashed = false)
    {
        for (int i = 0; i < seg; i++)
        {
            if (dashed && i % 2 == 1) continue;
            float a0 = i * Mathf.PI * 2 / seg, a1 = (i + 1) * Mathf.PI * 2 / seg;
            Seg(c + r * new Vector2(Mathf.Cos(a0), Mathf.Sin(a0)), c + r * new Vector2(Mathf.Cos(a1), Mathf.Sin(a1)), w, col);
        }
    }

    /// <summary>
    /// A wide polyline as one continuous strip with mitred joints: no overlapping caps, so the
    /// edges of a thick curve stay smooth. Consecutive duplicate points are skipped.
    /// </summary>
    public void Ribbon(IList<Vector2> raw, float w, Color col)
    {
        var pts = new List<Vector2>();
        foreach (var p in raw) if (pts.Count == 0 || (p - pts[pts.Count - 1]).sqrMagnitude > 1e-8f) pts.Add(p);
        if (pts.Count < 2) return;
        col = Lin(col);
        int i0 = _v.Count;
        float h = w * 0.5f;
        for (int i = 0; i < pts.Count; i++)
        {
            var dPrev = i > 0 ? (pts[i] - pts[i - 1]).normalized : (pts[1] - pts[0]).normalized;
            var dNext = i < pts.Count - 1 ? (pts[i + 1] - pts[i]).normalized : dPrev;
            var t = (dPrev + dNext).normalized;
            if (t.sqrMagnitude < 1e-6f) t = dNext;
            var n = new Vector2(-t.y, t.x);
            float miter = h / Mathf.Max(0.3f, Vector2.Dot(n, new Vector2(-dNext.y, dNext.x)));
            _v.Add(P(pts[i] - n * miter)); _v.Add(P(pts[i] + n * miter));
            _c.Add(col); _c.Add(col);
        }
        for (int i = 0; i + 1 < pts.Count; i++)
        {
            int a = i0 + 2 * i;
            _t.Add(a); _t.Add(a + 2); _t.Add(a + 3);
            _t.Add(a); _t.Add(a + 3); _t.Add(a + 1);
        }
    }

    public void Disc(Vector2 c, float r, Color col, int seg = 32)
    {
        col = Lin(col);
        int i0 = _v.Count;
        _v.Add(P(c)); _c.Add(col);
        for (int i = 0; i <= seg; i++)
        {
            float a = i * Mathf.PI * 2 / seg;
            _v.Add(P(c + r * new Vector2(Mathf.Cos(a), Mathf.Sin(a)))); _c.Add(col);
        }
        for (int i = 0; i < seg; i++) { _t.Add(i0); _t.Add(i0 + i + 2); _t.Add(i0 + i + 1); }
    }

    /// <summary>45° hatching clipped to a rectangle (safety-zone marking).</summary>
    public void Hatch(Vector2 min, Vector2 max, float spacing, float w, Color col)
    {
        for (float k = min.x - max.y; k <= max.x - min.y; k += spacing)
        {
            float z0 = Mathf.Max(min.y, min.x - k), z1 = Mathf.Min(max.y, max.x - k);
            if (z1 - z0 < 1e-4f) continue;
            Seg(new Vector2(z0 + k, z0), new Vector2(z1 + k, z1), w, col);
        }
    }

    public void Arrow(Vector2 from, Vector2 to, float w, Color col)
    {
        Seg(from, to, w, col);
        var d = (to - from).normalized;
        var n = new Vector2(-d.y, d.x);
        float k = w * 3.5f;
        col = Lin(col);
        int i = _v.Count;
        _v.Add(P(to + d * k)); _v.Add(P(to + n * k)); _v.Add(P(to - n * k));
        _c.Add(col); _c.Add(col); _c.Add(col);
        _t.Add(i); _t.Add(i + 1); _t.Add(i + 2);
    }

    public Mesh ToMesh(string name)
    {
        var m = new Mesh { name = name, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        m.SetVertices(_v);
        m.SetColors(_c);
        m.SetTriangles(_t, 0);
        m.RecalculateBounds();
        return m;
    }
}
