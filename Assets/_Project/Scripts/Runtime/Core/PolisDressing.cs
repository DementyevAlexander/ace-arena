using System;
using System.Collections.Generic;
using Arena.Sim;
using UnityEngine;

namespace Arena.Core
{
    /// <summary>Оформление этажа и хаба античным полисом. Препятствия арен Hades — городские объекты: мелкие соседние
    /// сливаются в один (выпуклая оболочка), каждый получает вид по форме: низкие — лавки и клумбы (через них стреляют),
    /// тонкие высокие — колонны, статуи, фонари, средние — постаменты со статуями и стенки, крупные — павильоны и портики
    /// (сквозь них не стреляют). Вокруг проходимого места — кварталы: дома, храмы, кипарисы (только вид, RoomDef.city).</summary>
    public static class PolisDressing
    {
        public const string Biome = "Polis";
        public const string Bench = "Polis/Bench", Planter = "Polis/Planter", Column = "Polis/Column", Statue = "Polis/Statue",
            Lamp = "Polis/Lamp", Pavilion = "Polis/Pavilion", Stoa = "Polis/Stoa", Block = "Polis/Block";
        public const string House = "Polis/House", Temple = "Polis/Temple", Cypress = "Polis/Cypress";

        const float SmallRadius = 1.1f, MergeGap = 0.7f, MaxMergeSize = 6f, HullSlack = 2.5f;

        // ---------- препятствия арены ----------

        /// <summary>Стены арены (уже на своих местах): мелкие соседние слиты, все видимые — городские объекты. Невидимые
        /// блокираторы Hades (высота 0) остаются как есть. keepClear — точки, которые оболочка не должна накрыть.</summary>
        public static List<RoomDef.Poly> DressArena(List<RoomDef.Poly> walls, bool indoor, IList<Vector2> keepClear, int seed)
        {
            var visible = new List<RoomDef.Poly>();
            var result = new List<RoomDef.Poly>();
            foreach (var w in walls) (w.h > 0f && w.p.Length >= 3 ? visible : result).Add(w);

            // слияние мелких: связаны, если их круги почти касаются
            int n = visible.Count;
            var c = new Vector2[n];
            var r = new float[n];
            for (int i = 0; i < n; i++) { c[i] = Geo.Centroid(visible[i].p); foreach (var v in visible[i].p) r[i] = Mathf.Max(r[i], (v - c[i]).magnitude); }
            var parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;
            int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
            for (int i = 0; i < n; i++)
            {
                if (r[i] > SmallRadius) continue;
                for (int j = i + 1; j < n; j++)
                    if (r[j] <= SmallRadius && (c[i] - c[j]).magnitude <= r[i] + r[j] + MergeGap) parent[Find(i)] = Find(j);
            }
            var groups = new Dictionary<int, List<int>>();
            for (int i = 0; i < n; i++) { int g = Find(i); if (!groups.TryGetValue(g, out var list)) groups[g] = list = new List<int>(); list.Add(i); }
            foreach (var g in groups.Values)
            {
                if (g.Count >= 2 && TryMerge(visible, g, keepClear, out var merged)) { result.Add(merged); continue; }
                foreach (int i in g) result.Add(visible[i]);
            }

            // вид каждого видимого объекта
            var rnd = new System.Random(seed);
            foreach (var w in result) if (w.h > 0f && w.p.Length >= 3) Classify(w, indoor, rnd);
            return result;
        }

        static bool TryMerge(List<RoomDef.Poly> polys, List<int> g, IList<Vector2> keepClear, out RoomDef.Poly merged)
        {
            merged = null;
            var pts = new List<Vector2>();
            float area = 0f, h = 0f;
            bool shots = false;
            foreach (int i in g)
            {
                pts.AddRange(polys[i].p);
                area += Mathf.Abs(Geo.SignedArea(polys[i].p));
                h = Mathf.Max(h, polys[i].h);
                shots |= polys[i].shots;
            }
            var hull = Hull(pts);
            if (hull.Length < 3) return false;
            var (len, wid) = Extent(hull);
            if (len > MaxMergeSize) return false;                                         // длинная цепочка — не сливать
            if (Mathf.Abs(Geo.SignedArea(hull)) > Mathf.Max(area, 0.5f) * HullSlack + 1.5f) return false; // оболочка накрыла бы проход
            foreach (var k in keepClear) if (Geo.InPoly(hull, k)) return false;
            merged = new RoomDef.Poly { p = hull, h = h, shots = shots, type = "Merged" };
            return true;
        }

        /// <summary>Вид объекта по форме: длина и ширина (по самому узкому направлению), площадь, держит ли снаряды.</summary>
        static void Classify(RoomDef.Poly w, bool indoor, System.Random rnd)
        {
            var (len, wid) = Extent(w.p);
            float area = Mathf.Abs(Geo.SignedArea(w.p));
            bool low = !w.shots || w.h <= 0.7f;
            bool longish = len > 2.2f * Mathf.Max(0.3f, wid);
            if (low) { w.type = longish ? Bench : Planter; w.h = 0.55f; w.shots = false; return; }
            if (len <= 1.5f)
            {
                w.type = indoor ? Column : rnd.Next(3) switch { 0 => Column, 1 => Statue, _ => Lamp };
                w.h = w.type == Column ? 3.4f : w.type == Statue ? 2.6f : 2.8f;
                return;
            }
            if (area <= 5f) { w.type = longish ? Stoa : Statue; w.h = longish ? 2.2f : 2.6f; return; }
            w.type = longish ? Stoa : Pavilion;
            w.h = longish ? 2.4f : 2.8f;
        }

        /// <summary>Длина и ширина многоугольника в его самом узком направлении (по рёбрам).</summary>
        public static (float len, float wid) Extent(Vector2[] p)
        {
            float bestArea = float.MaxValue, bl = 0f, bw = 0f;
            for (int i = 0; i < p.Length; i++)
            {
                var e = p[(i + 1) % p.Length] - p[i];
                if (e.sqrMagnitude < 1e-6f) continue;
                var u = e.normalized;
                var v = new Vector2(-u.y, u.x);
                float u0 = float.MaxValue, u1 = float.MinValue, v0 = float.MaxValue, v1 = float.MinValue;
                foreach (var q in p) { float a = Vector2.Dot(q, u), b = Vector2.Dot(q, v); u0 = Mathf.Min(u0, a); u1 = Mathf.Max(u1, a); v0 = Mathf.Min(v0, b); v1 = Mathf.Max(v1, b); }
                float l = u1 - u0, wd = v1 - v0;
                if (l * wd < bestArea) { bestArea = l * wd; bl = Mathf.Max(l, wd); bw = Mathf.Min(l, wd); }
            }
            return (bl, bw);
        }

        /// <summary>Выпуклая оболочка (монотонная цепочка Эндрю), против часовой.</summary>
        public static Vector2[] Hull(List<Vector2> pts)
        {
            var p = new List<Vector2>(pts);
            p.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
            if (p.Count < 3) return p.ToArray();
            var h = new Vector2[p.Count * 2];
            int k = 0;
            static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);
            for (int i = 0; i < p.Count; i++) { while (k >= 2 && Cross(h[k - 2], h[k - 1], p[i]) <= 0f) k--; h[k++] = p[i]; }
            for (int i = p.Count - 2, t = k + 1; i >= 0; i--) { while (k >= t && Cross(h[k - 2], h[k - 1], p[i]) <= 0f) k--; h[k++] = p[i]; }
            var res = new Vector2[k - 1];
            Array.Copy(h, res, k - 1);
            return res;
        }

        // ---------- что закрывает от камеры ----------

        static readonly Vector2 CamRight = new(0.70710678f, -0.70710678f), CamFwd = new(0.70710678f, 0.70710678f);
        const float CamSin = 0.5f, CamCos = 0.8660254f; // наклон камеры 30°

        /// <summary>Объект (центр c, радиус r, высота h) закрывает от камеры точку p на высоте y: стоит ближе к камере,
        /// перекрывает её по горизонтали экрана, и его верх на экране выше точки. Камера арены фиксирована (CameraRig:
        /// поворот 45°, наклон 30°), поэтому без самой камеры — геометрией экрана.</summary>
        public static bool Hides(Vector2 c, float r, float h, Vector2 p, float y)
        {
            if (h <= 0.3f) return false;
            if (Mathf.Abs(Vector2.Dot(c - p, CamRight)) > r + 0.3f) return false;
            float dc = Vector2.Dot(c, CamFwd), dp = Vector2.Dot(p, CamFwd);
            if (dc >= dp) return false; // за точкой или вровень — не закрывает
            return (dc + r) * CamSin + h * CamCos > dp * CamSin + y * CamCos;
        }

        /// <summary>Стены зала как закрывающие куски: по краю пола арены кусками до 1.5 ю, снаружи на 0.55 ю, высота 3.4;
        /// у дверей (ближе HallDoorClear) — проёмы. Те же правила, что у PolisKit.Hall.</summary>
        public const float HallHeight = 3.4f, HallDoorClear = 2.2f;

        public static List<(Vector2 c, float r)> HallPieces(Vector2[] floor, IList<Vector2> doors)
        {
            var list = new List<(Vector2, float)>();
            bool ccw = Geo.SignedArea(floor) > 0f;
            for (int i = 0; i < floor.Length; i++)
            {
                var a = floor[i];
                var b = floor[(i + 1) % floor.Length];
                var e = b - a;
                float len = e.magnitude;
                if (len < 0.05f) continue;
                var nrm = ccw ? new Vector2(e.y, -e.x) / len : new Vector2(-e.y, e.x) / len;
                int pieces = Mathf.Max(1, Mathf.CeilToInt(len / 1.5f));
                for (int k = 0; k < pieces; k++)
                {
                    var mid = Vector2.Lerp(a, b, (k + 0.5f) / pieces) + nrm * 0.55f;
                    bool door = false;
                    foreach (var d in doors) if ((d - mid).magnitude < HallDoorClear) { door = true; break; }
                    if (!door) list.Add((mid, len / pieces * 0.5f + 0.25f));
                }
            }
            return list;
        }

        // ---------- кварталы вокруг ----------

        /// <summary>Застройка по сетке 1 ю: near[i] — клетка рядом с проходимым (нельзя строить), может строить — клетка не дальше
        /// reach от проходимого. Дома 3–7 клеток вплотную друг к другу (улицы — между домами и проходимым), иногда храм,
        /// одиночные клетки у проходимого — кипарисы. origin — мировая точка клетки (0, 0).</summary>
        public static List<RoomDef.Poly> Build(bool[] blocked, bool[] buildable, int w, int h, Vector2 origin, int seed)
        {
            var list = new List<RoomDef.Poly>();
            var used = new bool[w * h];
            var rnd = new System.Random(seed);
            bool Free(int x, int y) => x >= 0 && y >= 0 && x < w && y < h && buildable[x + y * w] && !blocked[x + y * w] && !used[x + y * w];
            var order = new List<int>();
            for (int i = 0; i < w * h; i++) if (buildable[i] && !blocked[i]) order.Add(i);
            for (int i = order.Count - 1; i > 0; i--) { int j = rnd.Next(i + 1); (order[i], order[j]) = (order[j], order[i]); }
            foreach (int k in order)
            {
                int x0 = k % w, y0 = k / w;
                if (!Free(x0, y0)) continue;
                int tw = rnd.Next(3, 8), th = rnd.Next(3, 8);
                int bw = 0, bh = 0;
                // наибольший прямоугольник до tw×th, целиком свободный
                for (int ww = tw; ww >= 2 && bw == 0; ww--)
                    for (int hh = th; hh >= 2; hh--)
                    {
                        bool ok = true;
                        for (int yy = 0; yy < hh && ok; yy++) for (int xx = 0; xx < ww && ok; xx++) ok = Free(x0 + xx, y0 + yy);
                        if (ok) { bw = ww; bh = hh; break; }
                    }
                if (bw == 0)
                {
                    // одна клетка — кипарис (через раз, чтобы не стеной)
                    used[k] = true;
                    if (rnd.NextDouble() < 0.55)
                    {
                        var cpos = origin + new Vector2(x0 + 0.5f, y0 + 0.5f);
                        list.Add(new RoomDef.Poly { p = Square(cpos, 0.35f), type = Cypress, h = 2.6f + (float)rnd.NextDouble() * 1.4f, shots = false });
                    }
                    continue;
                }
                for (int yy = 0; yy < bh; yy++) for (int xx = 0; xx < bw; xx++) used[x0 + xx + (y0 + yy) * w] = true;
                var a = origin + new Vector2(x0, y0);
                var b = a + new Vector2(bw, bh);
                bool temple = bw >= 5 && bh >= 5 && rnd.NextDouble() < 0.25;
                float height = temple ? 3.6f : 2.6f + (float)rnd.NextDouble() * 2.4f;
                const float inset = 0.08f; // зазор между соседними домами — видно, где кончается один
                list.Add(new RoomDef.Poly
                {
                    p = new[] { a + new Vector2(inset, inset), new Vector2(b.x - inset, a.y + inset), b - new Vector2(inset, inset), new Vector2(a.x + inset, b.y - inset) },
                    type = temple ? Temple : House, h = height, shots = true,
                });
            }
            return list;
        }

        static Vector2[] Square(Vector2 c, float r) => new[] { c + new Vector2(-r, -r), c + new Vector2(r, -r), c + new Vector2(r, r), c + new Vector2(-r, r) };

        /// <summary>Маски для Build по многоугольникам пола (хаб — маленький): клетка занята, если до пола ближе minGap,
        /// строить можно не дальше reach.</summary>
        public static (bool[] blocked, bool[] buildable, int w, int h, Vector2 origin) MasksFromFloor(IList<RoomDef.Poly> floor, float minGap, float reach)
        {
            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            foreach (var f in floor) foreach (var v in f.p) { min = Vector2.Min(min, v); max = Vector2.Max(max, v); }
            var origin = new Vector2(Mathf.Floor(min.x - reach - 1f), Mathf.Floor(min.y - reach - 1f));
            int w = Mathf.CeilToInt(max.x + reach + 1f - origin.x), h = Mathf.CeilToInt(max.y + reach + 1f - origin.y);
            var dist = new float[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    var p = origin + new Vector2(x + 0.5f, y + 0.5f);
                    float d = float.MaxValue;
                    foreach (var f in floor)
                    {
                        if (Geo.InPoly(f.p, p)) { d = 0f; break; }
                        Geo.ClosestOnEdge(f.p, p, out float e);
                        d = Mathf.Min(d, e);
                    }
                    dist[x + y * w] = d;
                }
            var blocked = new bool[w * h];
            var buildable = new bool[w * h];
            for (int i = 0; i < w * h; i++) { blocked[i] = dist[i] < minGap + 0.71f; buildable[i] = dist[i] <= reach; }
            return (blocked, buildable, w, h, origin);
        }
    }
}
