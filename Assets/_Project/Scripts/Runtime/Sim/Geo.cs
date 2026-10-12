using System.Collections.Generic;
using UnityEngine;

namespace Arena.Sim
{
    /// <summary>Геометрия комнат Hades: многоугольники стен и препятствий, выталкивание кругов из них.</summary>
    public static class Geo
    {
        public static bool InPoly(IReadOnlyList<Vector2> poly, Vector2 p)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                var a = poly[i];
                var b = poly[j];
                if ((a.y > p.y) != (b.y > p.y) && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x) inside = !inside;
            }
            return inside;
        }

        public static Vector2 ClosestOnSegment(Vector2 a, Vector2 b, Vector2 p)
        {
            var ab = b - a;
            float l = ab.sqrMagnitude;
            if (l < 1e-8f) return a;
            return a + ab * Mathf.Clamp01(Vector2.Dot(p - a, ab) / l);
        }

        /// <summary>Ближайшая точка контура многоугольника.</summary>
        public static Vector2 ClosestOnEdge(IReadOnlyList<Vector2> poly, Vector2 p, out float dist)
        {
            var best = p;
            dist = float.MaxValue;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                var c = ClosestOnSegment(poly[j], poly[i], p);
                float d = (c - p).sqrMagnitude;
                if (d < dist) { dist = d; best = c; }
            }
            dist = Mathf.Sqrt(dist);
            return best;
        }

        /// <summary>Выталкивает круг из сплошного многоугольника. true — круг касался.</summary>
        public static bool PushOut(IReadOnlyList<Vector2> poly, ref Vector2 pos, float r)
        {
            var c = ClosestOnEdge(poly, pos, out float d);
            bool inside = InPoly(poly, pos);
            if (!inside && d >= r) return false;
            var n = pos - c;
            if (n.sqrMagnitude < 1e-10f) n = pos - Centroid(poly);
            n = n.sqrMagnitude < 1e-10f ? Vector2.up : n.normalized;
            pos = c + (inside ? -n : n) * r;
            return true;
        }

        /// <summary>Держит круг внутри многоугольника (контур пола).</summary>
        public static bool KeepInside(IReadOnlyList<Vector2> poly, ref Vector2 pos, float r)
        {
            var c = ClosestOnEdge(poly, pos, out float d);
            bool inside = InPoly(poly, pos);
            if (inside && d >= r) return false;
            var n = c - pos;
            if (n.sqrMagnitude < 1e-10f) n = Centroid(poly) - pos;
            n = n.sqrMagnitude < 1e-10f ? Vector2.up : n.normalized;
            pos = c + (inside ? -n : n) * r;
            return true;
        }

        /// <summary>Пересекает ли круг многоугольник (касание контура или центр внутри).</summary>
        public static bool Overlaps(IReadOnlyList<Vector2> poly, Vector2 p, float r)
        {
            if (InPoly(poly, p)) return true;
            ClosestOnEdge(poly, p, out float d);
            return d < r;
        }

        public static Vector2 Centroid(IReadOnlyList<Vector2> poly)
        {
            var s = Vector2.zero;
            for (int i = 0; i < poly.Count; i++) s += poly[i];
            return poly.Count > 0 ? s / poly.Count : s;
        }

        /// <summary>Первое пересечение отрезка a→b с контуром; t в долях отрезка.</summary>
        public static bool SegmentHit(IReadOnlyList<Vector2> poly, Vector2 a, Vector2 b, out float t)
        {
            t = 1f;
            bool hit = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
                if (SegSeg(a, b, poly[j], poly[i], out float u) && u < t) { t = u; hit = true; }
            return hit;
        }

        static bool SegSeg(Vector2 p, Vector2 p2, Vector2 q, Vector2 q2, out float t)
        {
            t = 0f;
            var r = p2 - p;
            var s = q2 - q;
            float den = r.x * s.y - r.y * s.x;
            if (Mathf.Abs(den) < 1e-9f) return false;
            var qp = q - p;
            t = (qp.x * s.y - qp.y * s.x) / den;
            float u = (qp.x * r.y - qp.y * r.x) / den;
            return t >= 0f && t <= 1f && u >= 0f && u <= 1f;
        }

        public static float SignedArea(IReadOnlyList<Vector2> poly)
        {
            float a = 0f;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++) a += poly[j].x * poly[i].y - poly[i].x * poly[j].y;
            return a * 0.5f;
        }

        /// <summary>Триангуляция простого многоугольника отсечением ушей. Индексы — в исходном порядке вершин.</summary>
        public static List<int> Triangulate(IReadOnlyList<Vector2> poly)
        {
            var tris = new List<int>();
            int n = poly.Count;
            if (n < 3) return tris;
            var idx = new List<int>(n);
            bool ccw = SignedArea(poly) > 0f;
            for (int i = 0; i < n; i++) idx.Add(ccw ? i : n - 1 - i);
            int guard = 0;
            while (idx.Count > 3 && guard++ < n * n)
            {
                bool clipped = false;
                for (int i = 0; i < idx.Count; i++)
                {
                    int a = idx[(i + idx.Count - 1) % idx.Count], b = idx[i], c = idx[(i + 1) % idx.Count];
                    var pa = poly[a];
                    var pb = poly[b];
                    var pc = poly[c];
                    if (Cross(pb - pa, pc - pb) <= 1e-7f) continue;
                    bool inside = false;
                    for (int k = 0; k < idx.Count && !inside; k++)
                    {
                        int q = idx[k];
                        if (q == a || q == b || q == c) continue;
                        if (InTri(poly[q], pa, pb, pc)) inside = true;
                    }
                    if (inside) continue;
                    tris.Add(a); tris.Add(b); tris.Add(c);
                    idx.RemoveAt(i);
                    clipped = true;
                    break;
                }
                if (!clipped) break; // вырожденный контур — оставшееся отбрасываем
            }
            if (idx.Count == 3) { tris.Add(idx[0]); tris.Add(idx[1]); tris.Add(idx[2]); }
            return tris;
        }

        static float Cross(Vector2 a, Vector2 b) => a.x * b.y - a.y * b.x;

        static bool InTri(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            float d1 = Cross(b - a, p - a), d2 = Cross(c - b, p - b), d3 = Cross(a - c, p - c);
            return d1 >= 0f && d2 >= 0f && d3 >= 0f;
        }
    }
}
