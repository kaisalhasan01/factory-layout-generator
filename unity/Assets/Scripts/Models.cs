using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Procedural 3D models, built from primitives at runtime: no imported meshes. Each builder works
/// in a local frame where the cell centre is the origin and +Z is the component's facing (for
/// conveyor modules: the direction of travel), which is the same contract as a PowerCopy's
/// origin point plus direction line in the CATIA builder.
/// </summary>
public static class Models
{
    public const float BeltTop = 0.80f;

    static readonly Dictionary<Color, Material> Cache = new Dictionary<Color, Material>();
    static Shader _standard;

    public static readonly Color Belt = new Color(0.17f, 0.18f, 0.20f);
    public static readonly Color Alu = new Color(0.72f, 0.75f, 0.78f);
    public static readonly Color Frame = new Color(0.42f, 0.46f, 0.49f);
    public static readonly Color RobotOrange = new Color(0.95f, 0.50f, 0.13f);
    public static readonly Color Dark = new Color(0.20f, 0.21f, 0.23f);
    public static readonly Color MachineBlue = new Color(0.24f, 0.43f, 0.62f);
    public static readonly Color Panel = new Color(0.86f, 0.88f, 0.90f);
    public static readonly Color Safety = new Color(0.95f, 0.76f, 0.19f);
    public static readonly Color Concrete = new Color(0.62f, 0.63f, 0.64f);
    public static readonly Color Go = new Color(0.23f, 0.67f, 0.36f);
    public static readonly Color StopRed = new Color(0.80f, 0.20f, 0.18f);
    public static readonly Color Cardboard = new Color(0.77f, 0.60f, 0.35f);

    public static Material Mat(Color c, float smooth = 0.25f, float metal = 0f)
    {
        if (Cache.TryGetValue(c, out var m)) return m;
        if (_standard == null) _standard = Shader.Find("Standard");
        m = new Material(_standard) { color = c };
        m.SetFloat("_Glossiness", smooth);
        m.SetFloat("_Metallic", metal);
        Cache[c] = m;
        return m;
    }

    public static Transform Node(string name, Transform parent, Vector3 pos, Quaternion rot)
    {
        var t = new GameObject(name).transform;
        t.SetParent(parent, false);
        t.localPosition = pos;
        t.localRotation = rot;
        return t;
    }

    public static Transform Part(Transform parent, PrimitiveType type, Vector3 pos, Vector3 scale, Color col,
        Quaternion? rot = null, float smooth = 0.25f, float metal = 0f)
    {
        var go = GameObject.CreatePrimitive(type);
        Object.Destroy(go.GetComponent<Collider>()); // logical grid only, never physics
        var t = go.transform;
        t.SetParent(parent, false);
        t.localPosition = pos;
        t.localRotation = rot ?? Quaternion.identity;
        t.localScale = scale;
        go.GetComponent<MeshRenderer>().sharedMaterial = Mat(col, smooth, metal);
        return t;
    }

    static Transform Box(Transform p, Vector3 pos, Vector3 size, Color c, Quaternion? rot = null, float smooth = 0.25f, float metal = 0f) =>
        Part(p, PrimitiveType.Cube, pos, size, c, rot, smooth, metal);

    /// <summary>Cylinder standing on its axis; <paramref name="height"/> is the real height.</summary>
    static Transform Cyl(Transform p, Vector3 pos, float diameter, float height, Color c, Quaternion? rot = null) =>
        Part(p, PrimitiveType.Cylinder, pos, new Vector3(diameter, height / 2f, diameter), c, rot);

    // ---------------------------------------------------------------- conveyor

    public static void ConveyorStraight(Transform root, float s)
    {
        float bw = 0.72f * s;
        Box(root, new Vector3(0, BeltTop - 0.015f, 0), new Vector3(bw, 0.03f, s), Belt, null, 0.1f);
        Box(root, new Vector3(0, BeltTop - 0.09f, 0), new Vector3(bw + 0.03f, 0.12f, s), Frame);
        foreach (var side in new[] { -1f, 1f })
        {
            Box(root, new Vector3(side * (bw / 2 + 0.015f), BeltTop + 0.01f, 0), new Vector3(0.03f, 0.08f, s), Alu, null, 0.6f, 0.6f);
            Box(root, new Vector3(side * bw / 2, (BeltTop - 0.15f) / 2, 0), new Vector3(0.04f, BeltTop - 0.15f, 0.04f), Frame);
        }
        Box(root, new Vector3(0, 0.12f, 0), new Vector3(bw, 0.03f, 0.03f), Frame);
    }

    /// <summary>A 90° module: enters at (0,-s/2) heading +Z, leaves at (∓s/2, 0).</summary>
    public static void ConveyorTurn(Transform root, float s, bool left)
    {
        float bw = 0.72f * s, R = s / 2;
        var c = new Vector2(left ? -s / 2 : s / 2, -s / 2);
        float a0 = left ? 0 : 180, a1 = 90;
        const int N = 8;
        for (int i = 0; i < N; i++)
        {
            float am = Mathf.Lerp(a0, a1, (i + 0.5f) / N) * Mathf.Deg2Rad;
            float da = Mathf.Abs(a1 - a0) / N * Mathf.Deg2Rad;
            var radial = new Vector2(Mathf.Cos(am), Mathf.Sin(am));
            var tangent = new Vector3(-radial.y, 0, radial.x);
            var rot = Quaternion.LookRotation(tangent);
            void Ring(float r, float y, Vector2 size, Color col, float smooth = 0.25f, float metal = 0f)
            {
                var p = c + radial * r;
                float len = 2 * r * Mathf.Sin(da / 2) * 1.08f + 0.004f;
                Box(root, new Vector3(p.x, y, p.y), new Vector3(size.x, size.y, len), col, rot, smooth, metal);
            }
            Ring(R, BeltTop - 0.015f, new Vector2(bw, 0.03f), Belt, 0.1f);
            Ring(R, BeltTop - 0.09f, new Vector2(bw + 0.03f, 0.12f), Frame);
            Ring(R - bw / 2 - 0.015f, BeltTop + 0.01f, new Vector2(0.03f, 0.08f), Alu, 0.6f, 0.6f);
            Ring(R + bw / 2 + 0.015f, BeltTop + 0.01f, new Vector2(0.03f, 0.08f), Alu, 0.6f, 0.6f);
        }
        float amid = (a0 + a1) / 2 * Mathf.Deg2Rad;
        var mid = c + R * new Vector2(Mathf.Cos(amid), Mathf.Sin(amid));
        Box(root,new Vector3(mid.x, (BeltTop - 0.15f) / 2, mid.y), new Vector3(0.05f, BeltTop - 0.15f, 0.05f), Frame);
    }

    /// <summary>Centre-line points of a turn module in its local frame, for products riding on it.</summary>
    public static Vector3[] TurnCentreLine(float s, bool left, int n = 8)
    {
        var c = new Vector2(left ? -s / 2 : s / 2, -s / 2);
        float a0 = left ? 0 : 180;
        var pts = new Vector3[n + 1];
        for (int i = 0; i <= n; i++)
        {
            float a = Mathf.Lerp(a0, 90, i / (float)n) * Mathf.Deg2Rad;
            var p = c + (s / 2) * new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            pts[i] = new Vector3(p.x, 0, p.y);
        }
        return pts;
    }

    // ---------------------------------------------------------------- stations

    public static void StartStation(Transform root, float s)
    {
        float bw = 0.72f * s;
        Box(root, new Vector3(0, BeltTop + 0.08f, -s / 2 + 0.03f), new Vector3(bw + 0.1f, 0.2f, 0.06f), Go);
        Cyl(root, new Vector3(bw / 2 + 0.09f, 0.7f, -s / 2 + 0.06f), 0.04f, 1.4f, Frame);
        Part(root, PrimitiveType.Sphere, new Vector3(bw / 2 + 0.09f, 1.45f, -s / 2 + 0.06f), Vector3.one * 0.12f, Go, null, 0.8f);
        Box(root, new Vector3(0, BeltTop + 0.09f, -0.02f), Vector3.one * 0.17f, Cardboard);
    }

    public static void StopStation(Transform root, float s)
    {
        float bw = 0.72f * s;
        Box(root, new Vector3(0, BeltTop + 0.08f, s / 2 - 0.03f), new Vector3(bw + 0.1f, 0.2f, 0.06f), StopRed);
        Cyl(root, new Vector3(-bw / 2 - 0.09f, 0.7f, s / 2 - 0.06f), 0.04f, 1.4f, Frame);
        Part(root, PrimitiveType.Sphere, new Vector3(-bw / 2 - 0.09f, 1.45f, s / 2 - 0.06f), Vector3.one * 0.12f, StopRed, null, 0.8f);
        for (int i = 0; i < 3; i++)
            Box(root, new Vector3(0.02f * (i % 2), 0.09f + i * 0.18f, s / 2 + 0.16f), Vector3.one * 0.17f, Cardboard,
                Quaternion.Euler(0, i * 9f, 0));
    }

    public sealed class RobotRig
    {
        public Transform Turret, Shoulder, Elbow;
        public float Phase;
        public float BaseYaw;
    }

    public static RobotRig Robot(Transform root, float s)
    {
        var rig = new RobotRig();
        Cyl(root, new Vector3(0, 0.05f, 0), 0.36f, 0.1f, Dark);
        Cyl(root, new Vector3(0, 0.36f, 0), 0.24f, 0.52f, Dark);
        rig.Turret = Node("turret", root, new Vector3(0, 0.62f, 0), Quaternion.identity);
        Cyl(rig.Turret, new Vector3(0, 0.04f, 0), 0.30f, 0.08f, RobotOrange);
        Box(rig.Turret, new Vector3(0, 0.14f, 0), new Vector3(0.2f, 0.14f, 0.2f), RobotOrange);
        rig.Shoulder = Node("shoulder", rig.Turret, new Vector3(0, 0.2f, 0), Quaternion.identity);
        Cyl(rig.Shoulder, Vector3.zero, 0.14f, 0.24f, Dark, Quaternion.Euler(0, 0, 90));
        Box(rig.Shoulder, new Vector3(0, 0.3f, 0), new Vector3(0.11f, 0.6f, 0.13f), RobotOrange);
        rig.Elbow = Node("elbow", rig.Shoulder, new Vector3(0, 0.6f, 0), Quaternion.identity);
        Cyl(rig.Elbow, Vector3.zero, 0.12f, 0.2f, Dark, Quaternion.Euler(0, 0, 90));
        Box(rig.Elbow, new Vector3(0, 0, 0.25f), new Vector3(0.09f, 0.09f, 0.5f), RobotOrange);
        var wrist = Node("wrist", rig.Elbow, new Vector3(0, 0, 0.5f), Quaternion.identity);
        Cyl(wrist, new Vector3(0, -0.06f, 0), 0.08f, 0.12f, Dark);
        Box(wrist, new Vector3(0, -0.14f, 0), new Vector3(0.12f, 0.03f, 0.05f), Alu, null, 0.6f, 0.6f);
        foreach (var side in new[] { -1f, 1f })
            Box(wrist, new Vector3(side * 0.05f, -0.2f, 0), new Vector3(0.02f, 0.1f, 0.04f), Alu, null, 0.6f, 0.6f);
        return rig;
    }

    public static void AnimateRobot(RobotRig rig, float time)
    {
        float t = time * 0.9f + rig.Phase;
        rig.Turret.localRotation = Quaternion.Euler(0, rig.BaseYaw + 55f * Mathf.Sin(t), 0);
        rig.Shoulder.localRotation = Quaternion.Euler(18f + 10f * Mathf.Sin(t * 2f), 0, 0);
        rig.Elbow.localRotation = Quaternion.Euler(-35f + 22f * Mathf.Sin(t * 2f + 0.6f), 0, 0);
    }

    public static void Drill(Transform root, float s)
    {
        Box(root, new Vector3(0, 0.04f, -0.02f), new Vector3(0.44f, 0.08f, 0.44f), Dark);
        Box(root, new Vector3(0, 1.0f, -0.12f), new Vector3(0.16f, 2.0f, 0.16f), MachineBlue);
        Box(root, new Vector3(0.12f, 1.0f, -0.12f), new Vector3(0.08f, 0.28f, 0.18f), Panel);
        // The head overhangs the conveyor in the cell the drill faces.
        Box(root, new Vector3(0, 1.62f, (s - 0.2f) / 2), new Vector3(0.24f, 0.3f, s + 0.3f), MachineBlue);
        Cyl(root, new Vector3(0, 1.36f, s), 0.12f, 0.25f, Panel);
        Cyl(root, new Vector3(0, 1.12f, s), 0.035f, 0.26f, Alu);
        Box(root, new Vector3(0, 1.79f, (s - 0.2f) / 2), new Vector3(0.25f, 0.04f, s + 0.31f), Safety);
    }

    /// <summary>Tunnel along +Z, <paramref name="cells"/> long, starting at the origin cell.</summary>
    public static void XRay(Transform root, float s, int cells)
    {
        float len = cells * s, zc = (cells - 1) * s / 2, w = s;
        foreach (var side in new[] { -1f, 1f })
            Box(root, new Vector3(side * (w / 2 - 0.03f), 0.72f, zc), new Vector3(0.06f, 1.44f, len), Panel);
        Box(root, new Vector3(0, 1.3f, zc), new Vector3(w, 0.3f, len), Panel);
        Box(root, new Vector3(0, 1.5f, zc), new Vector3(w + 0.01f, 0.08f, len + 0.01f), Safety);
        Box(root, new Vector3(0, 1.68f, zc), new Vector3(w * 0.8f, 0.28f, len * 0.6f), Panel);
        Box(root, new Vector3(w / 2 + 0.005f, 1.68f, zc), new Vector3(0.01f, 0.14f, 0.14f), Safety);
        foreach (var end in new[] { -s / 2, len - s / 2 })
            for (int i = 0; i < 4; i++)
                Box(root, new Vector3(-w * 0.3f + i * w * 0.2f, 1.06f, end), new Vector3(w * 0.18f, 0.18f, 0.01f), Dark);
    }

    /// <summary>
    /// An enclosed machining cell, <paramref name="length"/> cells along +Z (towards the loading
    /// side) and <paramref name="width"/> cells to the right (+X), from the origin cell.
    /// </summary>
    public static void Machine(Transform root, float s, int length, int width, Color colour)
    {
        float L = length * s, W = width * s;
        float cx = (width - 1) * s / 2, cz = (length - 1) * s / 2, front = (length - 0.5f) * s;
        var trim = Color.Lerp(colour, Color.white, 0.55f);
        var glass = new Color(0.10f, 0.16f, 0.22f);

        Box(root, new Vector3(cx, 0.06f, cz), new Vector3(W - 0.04f, 0.12f, L - 0.04f), Dark);
        Box(root, new Vector3(cx, 0.17f, cz), new Vector3(W - 0.05f, 0.1f, L - 0.05f), Safety);
        Box(root, new Vector3(cx, 1.2f, cz), new Vector3(W - 0.08f, 1.95f, L - 0.08f), colour, null, 0.35f);
        Box(root, new Vector3(cx, 2.2f, cz), new Vector3(W - 0.04f, 0.08f, L - 0.04f), trim, null, 0.4f);

        // Loading hatch on the front face, at belt height, centred.
        float hw = Mathf.Min(W - 0.25f, 0.62f);
        Box(root, new Vector3(cx, 1.02f, front - 0.03f), new Vector3(hw + 0.08f, 0.56f, 0.04f), Safety);
        Box(root, new Vector3(cx, 1.02f, front - 0.025f), new Vector3(hw, 0.46f, 0.05f), Dark);
        Box(root, new Vector3(cx, 1.55f, front - 0.03f), new Vector3(hw + 0.08f, 0.3f, 0.04f), trim);

        // Window along the right-hand side, if there is room.
        if (L >= 0.9f)
            Box(root, new Vector3(cx + W / 2 - 0.035f, 1.35f, cz), new Vector3(0.02f, 0.7f, L * 0.6f), glass, null, 0.9f);

        // Control panel at the front-left corner, and a stack light on the roof.
        Box(root, new Vector3(cx - W / 2 + 0.2f, 1.25f, front - 0.06f), new Vector3(0.24f, 0.36f, 0.1f), Panel);
        Box(root, new Vector3(cx - W / 2 + 0.2f, 1.3f, front - 0.005f), new Vector3(0.16f, 0.12f, 0.01f), new Color(0.2f, 0.75f, 0.85f), null, 0.9f);
        var lamp = new Vector3(cx - W / 2 + 0.15f, 2.24f, front - 0.15f);
        Cyl(root, lamp + new Vector3(0, 0.1f, 0), 0.03f, 0.2f, Frame);
        Cyl(root, lamp + new Vector3(0, 0.25f, 0), 0.08f, 0.1f, Go);
        Cyl(root, lamp + new Vector3(0, 0.35f, 0), 0.08f, 0.1f, Safety);
        Cyl(root, lamp + new Vector3(0, 0.45f, 0), 0.08f, 0.1f, StopRed);
    }

    public static void Pillar(Transform root, float s)
    {
        Box(root, new Vector3(0, 1.5f, 0), new Vector3(0.8f * s, 3.0f, 0.8f * s), Concrete, null, 0.05f);
        Box(root, new Vector3(0, 0.3f, 0), new Vector3(0.9f * s, 0.6f, 0.9f * s), Safety);
        foreach (var y in new[] { 0.15f, 0.45f })
            Box(root, new Vector3(0, y, 0), new Vector3(0.91f * s, 0.08f, 0.91f * s), Dark);
    }
}
