using System.Collections.Generic;
using UnityEngine;

namespace Arena.View
{
    /// <summary>Low-poly меши с плоским затенением (каждый треугольник со своими вершинами), как в TUNIC.</summary>
    public static class MeshKit
    {
        static readonly Dictionary<string, Mesh> Cache = new();

        sealed class Builder
        {
            readonly List<Vector3> v = new();
            readonly List<int> t = new();
            readonly List<Vector2> uv = new();
            /// <summary>Центр выпуклой фигуры: грани разворачиваются наружу от него (лицевая сторона в Unity — по часовой).</summary>
            public Vector3 Center;
            /// <summary>Не разворачивать грани: порядок вершин задан вызывающим (невыпуклые призмы).</summary>
            public bool Raw;

            public void Tri(Vector3 a, Vector3 b, Vector3 c)
            {
                if (!Raw && Vector3.Dot(Vector3.Cross(b - a, c - a), (a + b + c) / 3f - Center) < 0f) (b, c) = (c, b);
                int i = v.Count;
                v.Add(a); v.Add(b); v.Add(c);
                uv.Add(Vector2.zero); uv.Add(Vector2.right); uv.Add(Vector2.up);
                t.Add(i); t.Add(i + 1); t.Add(i + 2);
            }

            public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d) { Tri(a, b, c); Tri(a, c, d); }

            public Mesh Build(string name)
            {
                var m = new Mesh { name = name, indexFormat = v.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
                m.SetVertices(v); m.SetTriangles(t, 0); m.SetUVs(0, uv);
                m.RecalculateNormals(); m.RecalculateBounds();
                return m;
            }
        }

        static Mesh Cached(string key, System.Func<Mesh> make)
        {
            if (!Cache.TryGetValue(key, out var m) || m == null) Cache[key] = m = make();
            return m;
        }

        /// <summary>Икосфера радиуса 0.5; subdiv 0 — 20 граней, 1 — 80.</summary>
        public static Mesh Ico(int subdiv = 1, float jitter = 0f, int seed = 0) => Cached($"ico{subdiv}_{jitter}_{seed}", () =>
        {
            float p = (1f + Mathf.Sqrt(5f)) / 2f;
            var verts = new List<Vector3>
            {
                new(-1, p, 0), new(1, p, 0), new(-1, -p, 0), new(1, -p, 0), new(0, -1, p), new(0, 1, p),
                new(0, -1, -p), new(0, 1, -p), new(p, 0, -1), new(p, 0, 1), new(-p, 0, -1), new(-p, 0, 1),
            };
            var faces = new List<int>
            {
                0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11, 1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
                3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9, 4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1,
            };
            for (int s = 0; s < subdiv; s++)
            {
                var mid = new Dictionary<long, int>();
                int Mid(int a, int b)
                {
                    long k = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                    if (mid.TryGetValue(k, out int r)) return r;
                    verts.Add((verts[a] + verts[b]) * 0.5f);
                    return mid[k] = verts.Count - 1;
                }
                var nf = new List<int>();
                for (int i = 0; i < faces.Count; i += 3)
                {
                    int a = faces[i], b = faces[i + 1], c = faces[i + 2], ab = Mid(a, b), bc = Mid(b, c), ca = Mid(c, a);
                    nf.AddRange(new[] { a, ab, ca, b, bc, ab, c, ca, bc, ab, bc, ca });
                }
                faces = nf;
            }
            var rnd = new System.Random(seed);
            for (int i = 0; i < verts.Count; i++)
            {
                float j = 1f + (jitter > 0f ? ((float)rnd.NextDouble() * 2f - 1f) * jitter : 0f);
                verts[i] = verts[i].normalized * 0.5f * j;
            }
            var bld = new Builder();
            for (int i = 0; i < faces.Count; i += 3) bld.Tri(verts[faces[i]], verts[faces[i + 1]], verts[faces[i + 2]]);
            return bld.Build("Ico");
        });

        /// <summary>Призма: n граней, радиус 0.5, высота 1, основание на y = 0. top — множитель радиуса сверху (конус при 0).</summary>
        public static Mesh Prism(int n, float top = 1f, bool caps = true) => Cached($"prism{n}_{top}_{caps}", () =>
        {
            var b = new Builder { Center = new Vector3(0f, top > 0.001f ? 0.5f : 0.3f, 0f) };
            for (int i = 0; i < n; i++)
            {
                float a0 = i * Mathf.PI * 2f / n, a1 = (i + 1) * Mathf.PI * 2f / n;
                var p0 = new Vector3(Mathf.Cos(a0), 0, Mathf.Sin(a0)) * 0.5f;
                var p1 = new Vector3(Mathf.Cos(a1), 0, Mathf.Sin(a1)) * 0.5f;
                var q0 = p0 * top + Vector3.up;
                var q1 = p1 * top + Vector3.up;
                if (top > 0.001f) b.Quad(p0, q0, q1, p1); else b.Tri(p0, Vector3.up, p1);
                if (caps)
                {
                    b.Tri(Vector3.zero, p0, p1);
                    if (top > 0.001f) b.Tri(Vector3.up, q1, q0);
                }
            }
            return b.Build("Prism");
        });

        public static Mesh Box() => Cached("box", () =>
        {
            var b = new Builder();
            Vector3 P(float x, float y, float z) => new(x - 0.5f, y - 0.5f, z - 0.5f);
            b.Quad(P(0, 0, 0), P(0, 1, 0), P(1, 1, 0), P(1, 0, 0));
            b.Quad(P(1, 0, 1), P(1, 1, 1), P(0, 1, 1), P(0, 0, 1));
            b.Quad(P(0, 0, 1), P(0, 1, 1), P(0, 1, 0), P(0, 0, 0));
            b.Quad(P(1, 0, 0), P(1, 1, 0), P(1, 1, 1), P(1, 0, 1));
            b.Quad(P(0, 1, 0), P(0, 1, 1), P(1, 1, 1), P(1, 1, 0));
            b.Quad(P(0, 0, 1), P(0, 0, 0), P(1, 0, 0), P(1, 0, 1));
            return b.Build("Box");
        });

        /// <summary>Плоский квадрат 1×1 на полу (XZ), UV 0..1, для декалей.</summary>
        public static Mesh FloorQuad() => Cached("floorquad", () =>
        {
            var m = new Mesh { name = "FloorQuad" };
            m.vertices = new[] { new Vector3(-0.5f, 0, -0.5f), new Vector3(-0.5f, 0, 0.5f), new Vector3(0.5f, 0, 0.5f), new Vector3(0.5f, 0, -0.5f) };
            m.uv = new[] { new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0) };
            m.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            m.RecalculateNormals(); m.RecalculateBounds();
            return m;
        });

        /// <summary>Полоса на полу: от z = 0 до z = 1, ширина 1.</summary>
        public static Mesh FloorLane() => Cached("floorlane", () =>
        {
            var m = new Mesh { name = "FloorLane" };
            m.vertices = new[] { new Vector3(-0.5f, 0, 0), new Vector3(-0.5f, 0, 1), new Vector3(0.5f, 0, 1), new Vector3(0.5f, 0, 0) };
            m.uv = new[] { new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0) };
            m.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            m.RecalculateNormals(); m.RecalculateBounds();
            return m;
        });

        /// <summary>Сектор на полу радиуса 1 с полууглом half (градусы), вершина в центре, ось — +Z.</summary>
        public static Mesh FloorFan(float half) => Cached($"fan{half}", () =>
        {
            int n = 16;
            var v = new List<Vector3> { Vector3.zero };
            var uv = new List<Vector2> { new(0.5f, 0.5f) };
            var t = new List<int>();
            for (int i = 0; i <= n; i++)
            {
                float a = Mathf.Deg2Rad * (-half + 2f * half * i / n);
                var p = new Vector3(Mathf.Sin(a), 0, Mathf.Cos(a));
                v.Add(p); uv.Add(new Vector2(0.5f + p.x * 0.5f, 0.5f + p.z * 0.5f));
                if (i > 0) { t.Add(0); t.Add(i); t.Add(i + 1); }
            }
            var m = new Mesh { name = "Fan" };
            m.SetVertices(v); m.SetUVs(0, uv); m.SetTriangles(t, 0);
            m.RecalculateNormals(); m.RecalculateBounds();
            return m;
        });

        /// <summary>Треугольник-крыло (для мыши Dio и стрелки прицела).</summary>
        public static Mesh Wedge() => Cached("wedge", () =>
        {
            var b = new Builder { Center = new Vector3(0, 0.05f, -0.2f) };
            var a = new Vector3(0, 0, 0.5f);
            var l = new Vector3(-0.35f, 0, -0.5f);
            var r = new Vector3(0.35f, 0, -0.5f);
            var top = new Vector3(0, 0.15f, -0.2f);
            b.Tri(a, top, r); b.Tri(a, l, top); b.Tri(l, r, top); b.Tri(a, r, l);
            return b.Build("Wedge");
        });

        /// <summary>Плоский многоугольник на высоте y (контур пола), нормаль вверх. Точки — плоскость пола (x, z).</summary>
        public static Mesh Polygon(IReadOnlyList<Vector2> poly, float y, float uvScale = 0.5f)
        {
            var v = new List<Vector3>(poly.Count);
            var uv = new List<Vector2>(poly.Count);
            foreach (var p in poly) { v.Add(new Vector3(p.x, y, p.y)); uv.Add(p * uvScale); }
            var tris = Arena.Sim.Geo.Triangulate(poly);
            // треугольники в порядке против часовой при виде сверху (x, z) → для Unity нужен обратный
            for (int i = 0; i < tris.Count; i += 3) (tris[i + 1], tris[i + 2]) = (tris[i + 2], tris[i + 1]);
            var m = new Mesh { name = "Polygon", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            m.SetVertices(v); m.SetUVs(0, uv); m.SetTriangles(tris, 0);
            m.RecalculateNormals(); m.RecalculateBounds();
            return m;
        }

        /// <summary>Призма по контуру: стенки и крышка на высоте h (стены и препятствия комнаты), плоские грани.</summary>
        public static Mesh Extrude(IReadOnlyList<Vector2> poly, float h, float bottom = 0f)
        {
            var b = new Builder { Raw = true };
            bool ccw = Arena.Sim.Geo.SignedArea(poly) > 0f;
            int n = poly.Count;
            for (int i = 0; i < n; i++)
            {
                var a2 = poly[i];
                var c2 = poly[(i + 1) % n];
                if (!ccw) (a2, c2) = (c2, a2);
                var a0 = new Vector3(a2.x, bottom, a2.y);
                var c0 = new Vector3(c2.x, bottom, c2.y);
                var a1 = new Vector3(a2.x, h, a2.y);
                var c1 = new Vector3(c2.x, h, c2.y);
                b.Tri(a0, c1, c0); b.Tri(a0, a1, c1);
            }
            var tris = Arena.Sim.Geo.Triangulate(poly);
            for (int i = 0; i < tris.Count; i += 3)
            {
                var p0 = poly[tris[i]];
                var p1 = poly[tris[i + 1]];
                var p2 = poly[tris[i + 2]];
                b.Tri(new Vector3(p0.x, h, p0.y), new Vector3(p2.x, h, p2.y), new Vector3(p1.x, h, p1.y));
            }
            return b.Build("Extrude");
        }
    }
}
