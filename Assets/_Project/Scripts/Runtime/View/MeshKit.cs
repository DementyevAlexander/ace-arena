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

        /// <summary>Квадрат 1×1 в плоскости XY лицом к −Z — для значков с Billboard (смотрят в камеру).</summary>
        public static Mesh Billboard() => Cached("billboard", () =>
        {
            var m = new Mesh { name = "Billboard" };
            m.vertices = new[] { new Vector3(-0.5f, -0.5f, 0), new Vector3(-0.5f, 0.5f, 0), new Vector3(0.5f, 0.5f, 0), new Vector3(0.5f, -0.5f, 0) };
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

        /// <summary>Трапеция на полу: от z = 0 (полуширина r) до z = 1 (полуширина 1), UV как у полосы.</summary>
        public static Mesh FloorTrapezoid(float r) => Cached($"trap{r:0.00}", () =>
        {
            var m = new Mesh { name = "FloorTrapezoid" };
            m.vertices = new[] { new Vector3(-r, 0, 0), new Vector3(-1, 0, 1), new Vector3(1, 0, 1), new Vector3(r, 0, 0) };
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

        /// <summary>Обводка замкнутого многоугольника на полу (x, z) одинаковой толщины width внутрь фигуры: на изгибах вершина
        /// внутреннего края сдвигается по биссектрисе, чтобы толщина не менялась. Пишет в переданный меш (его можно перестраивать).</summary>
        public static void Stroke(Mesh m, IReadOnlyList<Vector2> poly, float width)
        {
            int n = poly.Count;
            float area = 0f;
            for (int i = 0; i < n; i++) { var a = poly[i]; var b = poly[(i + 1) % n]; area += a.x * b.y - b.x * a.y; }
            float sign = area >= 0f ? 1f : -1f; // против часовой — внутренняя нормаль слева
            var inner = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                var p0 = poly[(i + n - 1) % n]; var p1 = poly[i]; var p2 = poly[(i + 1) % n];
                var d0 = (p1 - p0).normalized; var d1 = (p2 - p1).normalized;
                var n0 = new Vector2(-d0.y, d0.x) * sign; var n1 = new Vector2(-d1.y, d1.x) * sign;
                var mid = n0 + n1;
                mid = mid.sqrMagnitude < 1e-6f ? n0 : mid.normalized;
                float k = Mathf.Max(Vector2.Dot(mid, n0), 0.35f);
                inner[i] = p1 + mid * (width / k);
            }
            var v = new List<Vector3>(n * 2);
            var t = new List<int>(n * 12);
            for (int i = 0; i < n; i++) { v.Add(new Vector3(poly[i].x, 0f, poly[i].y)); v.Add(new Vector3(inner[i].x, 0f, inner[i].y)); }
            for (int i = 0; i < n; i++)
            {
                int a = i * 2, b = a + 1, c = (i + 1) % n * 2, d = c + 1;
                // обе стороны: какая лицевая, зависит от обхода; лишняя отсекается
                t.Add(a); t.Add(c); t.Add(d); t.Add(a); t.Add(d); t.Add(b);
                t.Add(a); t.Add(d); t.Add(c); t.Add(a); t.Add(b); t.Add(d);
            }
            m.Clear();
            m.SetVertices(v); m.SetTriangles(t, 0);
            m.RecalculateNormals(); m.RecalculateBounds();
        }

        /// <summary>Многоугольник препятствия арены (x, z против часовой стрелки), поднятый на высоту h: стенки и крыша.
        /// Не кэшируется — у каждого препятствия своя форма. Многоугольник может быть невыпуклым.</summary>
        public static Mesh Extrude(IReadOnlyList<Vector2> poly, float h, bool sides = true, bool top = true)
        {
            var v = new List<Vector3>();
            var t = new List<int>();
            void Tri(Vector3 a, Vector3 b, Vector3 c) { int i = v.Count; v.Add(a); v.Add(b); v.Add(c); t.Add(i); t.Add(i + 1); t.Add(i + 2); }
            if (sides)
                for (int i = 0; i < poly.Count; i++)
                {
                    var a = poly[i]; var b = poly[(i + 1) % poly.Count];
                    Vector3 a0 = new(a.x, 0f, a.y), b0 = new(b.x, 0f, b.y), a1 = a0 + Vector3.up * h, b1 = b0 + Vector3.up * h;
                    // снаружи ребро a→b идёт слева направо: по часовой — a0, a1, b1, b0
                    Tri(a0, a1, b1); Tri(a0, b1, b0);
                }
            if (top)
                foreach (var (a, b, c) in Triangulate(poly))
                    Tri(new Vector3(a.x, h, a.y), new Vector3(c.x, h, c.y), new Vector3(b.x, h, b.y)); // сверху — по часовой
            var m = new Mesh { name = "Extrude", indexFormat = v.Count > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
            m.SetVertices(v); m.SetTriangles(t, 0);
            m.RecalculateNormals(); m.RecalculateBounds();
            return m;
        }

        /// <summary>Разбиение многоугольника (против часовой стрелки) на треугольники отсечением ушей.</summary>
        static IEnumerable<(Vector2, Vector2, Vector2)> Triangulate(IReadOnlyList<Vector2> poly)
        {
            var idx = new List<int>();
            for (int i = 0; i < poly.Count; i++) idx.Add(i);
            static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
            int guard = 0;
            while (idx.Count > 3 && guard++ < 4 * poly.Count)
            {
                bool cut = false;
                for (int k = 0; k < idx.Count; k++)
                {
                    Vector2 a = poly[idx[(k + idx.Count - 1) % idx.Count]], b = poly[idx[k]], c = poly[idx[(k + 1) % idx.Count]];
                    if (Cross(a, b, c) <= 0f) continue; // вогнутый угол
                    bool empty = true;
                    foreach (int j in idx)
                    {
                        var p = poly[j];
                        if (p == a || p == b || p == c) continue;
                        if (Cross(a, b, p) >= 0f && Cross(b, c, p) >= 0f && Cross(c, a, p) >= 0f) { empty = false; break; }
                    }
                    if (!empty) continue;
                    yield return (a, b, c);
                    idx.RemoveAt(k);
                    cut = true;
                    break;
                }
                if (!cut) break; // самопересечение — остаток веером
            }
            for (int k = 1; k + 1 < idx.Count; k++) yield return (poly[idx[0]], poly[idx[k]], poly[idx[k + 1]]);
        }

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
        public static Mesh Slab(IReadOnlyList<Vector2> poly, float h, float bottom = 0f)
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
