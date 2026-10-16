using System.Collections.Generic;
using Arena.Core;
using Arena.Sim;
using UnityEngine;

namespace Arena.View
{
    /// <summary>Объекты античного полиса из крупных простых форм (low-poly, как TUNIC): на аренах — колонны, статуи, фонари,
    /// лавки, клумбы, павильоны, портики; вокруг — дома с черепичными крышами, храмы, кипарисы. Цвета — палитра биома
    /// Polis: мрамор, терракота, бронза, тёмная зелень.</summary>
    public static class PolisKit
    {
        sealed class Mats
        {
            public Material Marble, MarbleDark, Roof, Bronze, Leaf, Cypress, Glow, Trunk;
            public Material[] Plaster;
        }

        static Mats mats;
        static RoomView.Palette matsFor;

        static Mats M(RoomView.Palette pal)
        {
            if (mats != null && matsFor == pal) return mats;
            matsFor = pal;
            return mats = new Mats
            {
                Marble = Art.LitMat(pal.Wall),
                MarbleDark = Art.LitMat(Color.Lerp(pal.Wall, new Color(0.45f, 0.42f, 0.4f), 0.3f)),
                Roof = Art.LitMat(pal.WallTop),
                Bronze = Art.LitMat(new Color(0.5f, 0.38f, 0.24f)),
                Leaf = Art.LitMat(new Color(0.3f, 0.5f, 0.28f)),
                Cypress = Art.LitMat(new Color(0.14f, 0.32f, 0.2f)),
                Glow = Art.LitMat(new Color(1f, 0.78f, 0.42f), 1.1f),
                Trunk = Art.LitMat(new Color(0.4f, 0.3f, 0.22f)),
                Plaster = new[]
                {
                    Art.LitMat(pal.Wall), Art.LitMat(new Color(0.93f, 0.86f, 0.72f)), Art.LitMat(new Color(0.86f, 0.76f, 0.6f)),
                    Art.LitMat(new Color(0.9f, 0.82f, 0.78f)),
                },
            };
        }

        static Transform Group(Transform parent, string name)
        {
            var t = new GameObject(name).transform;
            t.SetParent(parent, false);
            return t;
        }

        static void Extrude(Transform t, Vector2[] poly, float top, float bottom, Material m, bool shadows = true)
        {
            var go = new GameObject("Extrude");
            go.transform.SetParent(t, false);
            go.AddComponent<MeshFilter>().sharedMesh = MeshKit.Slab(poly, top, bottom);
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = m;
            r.shadowCastingMode = shadows ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        /// <summary>Контур, раздвинутый от центра на d (для выпуклых — как отступ).</summary>
        public static Vector2[] Grow(Vector2[] p, float d)
        {
            var c = Geo.Centroid(p);
            var o = new Vector2[p.Length];
            for (int i = 0; i < p.Length; i++)
            {
                var v = p[i] - c;
                float l = v.magnitude;
                o[i] = l > 1e-4f ? p[i] + v / l * d : p[i];
            }
            return o;
        }

        /// <summary>Четырёхскатная крыша над прямоугольником (оси мира) от высоты y, высота конька rise.</summary>
        static void HipRoof(Transform t, Vector2 a, Vector2 b, float y, float rise, float overhang, Material m)
        {
            var c = (a + b) * 0.5f;
            var holder = new GameObject("Roof").transform;
            holder.SetParent(t, false);
            holder.localPosition = new Vector3(c.x, y, c.y);
            holder.localScale = new Vector3(Mathf.Abs(b.x - a.x) + overhang * 2f, rise, Mathf.Abs(b.y - a.y) + overhang * 2f);
            // ромб призмы (радиус 0.5), повёрнутый на 45°, — квадрат со стороной 1 после масштаба √2
            Art.Part(holder, MeshKit.Prism(4, 0f), m, Vector3.zero, new Vector3(1.4142f, 1f, 1.4142f), new Vector3(0f, 45f, 0f));
        }

        // ---------- на арене ----------

        /// <summary>Объект арены по его виду (PolisDressing). Возвращает корень (для прозрачности) или null.</summary>
        public static GameObject Prop(Transform parent, string type, Vector2[] poly, RoomView.Palette pal, int seed)
        {
            var m = M(pal);
            var t = Group(parent, type);
            var c = Geo.Centroid(poly);
            var (len, wid) = PolisDressing.Extent(poly);
            switch (type)
            {
                case PolisDressing.Bench:
                    Extrude(t, poly, 0.4f, 0f, m.MarbleDark);
                    Extrude(t, Grow(poly, 0.07f), 0.5f, 0.4f, m.Marble);
                    break;
                case PolisDressing.Planter:
                {
                    Extrude(t, poly, 0.5f, 0f, m.MarbleDark);
                    float s = Mathf.Clamp(wid * 0.9f, 0.5f, 2.2f);
                    Art.Part(t, MeshKit.Ico(1, 0.15f, seed), m.Leaf, new Vector3(c.x, 0.85f, c.y), new Vector3(s, Mathf.Min(1.1f, s), s));
                    break;
                }
                case PolisDressing.Column:
                {
                    float r = Mathf.Clamp(wid * 0.5f, 0.28f, 0.7f);
                    Art.Part(t, MeshKit.Prism(8), m.MarbleDark, new Vector3(c.x, 0f, c.y), new Vector3(r * 2.3f, 0.28f, r * 2.3f));
                    Art.Part(t, MeshKit.Prism(12, 0.88f), m.Marble, new Vector3(c.x, 0.28f, c.y), new Vector3(r * 1.6f, 2.9f, r * 1.6f));
                    Art.Part(t, MeshKit.Box(), m.Marble, new Vector3(c.x, 3.3f, c.y), new Vector3(r * 2.4f, 0.26f, r * 2.4f));
                    break;
                }
                case PolisDressing.Statue:
                {
                    Extrude(t, poly, 1.0f, 0f, m.Marble);
                    float s = Mathf.Clamp(wid * 0.55f, 0.4f, 1.2f);
                    Art.Part(t, MeshKit.Prism(8, 0.35f), m.Bronze, new Vector3(c.x, 1.0f, c.y), new Vector3(s, 1.3f, s));
                    Art.Part(t, MeshKit.Ico(1), m.Bronze, new Vector3(c.x, 2.45f, c.y), Vector3.one * s * 0.5f);
                    break;
                }
                case PolisDressing.Lamp:
                    Extrude(t, poly, 0.3f, 0f, m.MarbleDark);
                    Art.Part(t, MeshKit.Prism(8), m.Bronze, new Vector3(c.x, 0.3f, c.y), new Vector3(0.16f, 2.2f, 0.16f));
                    Art.Part(t, MeshKit.Box(), m.Glow, new Vector3(c.x, 2.65f, c.y), new Vector3(0.36f, 0.4f, 0.36f), new Vector3(0f, 45f, 0f), false);
                    Art.Part(t, MeshKit.Prism(4, 0f), m.Bronze, new Vector3(c.x, 2.85f, c.y), new Vector3(0.6f, 0.3f, 0.6f));
                    break;
                case PolisDressing.Pavilion:
                    Extrude(t, Grow(poly, 0.08f), 0.3f, 0f, m.MarbleDark);
                    Extrude(t, Grow(poly, -0.12f), 2.3f, 0.3f, m.Marble);
                    Extrude(t, Grow(poly, 0.3f), 2.62f, 2.3f, m.Roof);
                    break;
                case PolisDressing.Stoa:
                    Extrude(t, poly, 2.0f, 0f, m.Marble);
                    Extrude(t, Grow(poly, 0.12f), 2.24f, 2.0f, m.Roof);
                    break;
                default: // Block
                    Extrude(t, poly, 1.3f, 0f, m.Marble);
                    Extrude(t, Grow(poly, 0.06f), 1.4f, 1.3f, m.MarbleDark);
                    break;
            }
            return t.gameObject;
        }

        // ---------- вокруг ----------

        /// <summary>Дом, храм или кипарис (RoomDef.city).</summary>
        public static GameObject Building(Transform parent, RoomDef.Poly b, RoomView.Palette pal, System.Random rnd)
        {
            var m = M(pal);
            var t = Group(parent, b.type);
            var p = b.p;
            var min = Vector2.Min(Vector2.Min(p[0], p[1]), Vector2.Min(p[2], p[3]));
            var max = Vector2.Max(Vector2.Max(p[0], p[1]), Vector2.Max(p[2], p[3]));
            var c = (min + max) * 0.5f;
            switch (b.type)
            {
                case PolisDressing.Cypress:
                    Art.Part(t, MeshKit.Prism(6), m.Trunk, new Vector3(c.x, 0f, c.y), new Vector3(0.2f, 0.5f, 0.2f));
                    Art.Part(t, MeshKit.Prism(8, 0f), m.Cypress, new Vector3(c.x, 0.35f, c.y), new Vector3(0.95f, b.h, 0.95f));
                    break;
                case PolisDressing.Temple:
                {
                    float h = b.h;
                    Extrude(t, p, 0.4f, 0f, m.MarbleDark);
                    Extrude(t, Grow(p, -0.9f), h - 0.4f, 0.4f, m.Marble); // целла
                    // колоннада по периметру
                    var a = min + new Vector2(0.45f, 0.45f);
                    var z = max - new Vector2(0.45f, 0.45f);
                    var cols = new List<Vector2>();
                    int nx = Mathf.Max(2, Mathf.RoundToInt((z.x - a.x) / 1.15f) + 1), ny = Mathf.Max(2, Mathf.RoundToInt((z.y - a.y) / 1.15f) + 1);
                    for (int i = 0; i < nx; i++) { float x = Mathf.Lerp(a.x, z.x, i / (nx - 1f)); cols.Add(new Vector2(x, a.y)); cols.Add(new Vector2(x, z.y)); }
                    for (int j = 1; j < ny - 1; j++) { float y = Mathf.Lerp(a.y, z.y, j / (ny - 1f)); cols.Add(new Vector2(a.x, y)); cols.Add(new Vector2(z.x, y)); }
                    foreach (var q in cols) Art.Part(t, MeshKit.Prism(10, 0.9f), m.Marble, new Vector3(q.x, 0.4f, q.y), new Vector3(0.5f, h - 0.8f, 0.5f));
                    Extrude(t, Grow(p, 0.05f), h, h - 0.4f, m.Marble);
                    HipRoof(t, min, max, h, 0.9f, 0.2f, m.Roof);
                    break;
                }
                default: // дом
                {
                    var wall = m.Plaster[rnd.Next(m.Plaster.Length)];
                    Extrude(t, p, b.h, 0f, wall);
                    Extrude(t, Grow(p, 0.06f), 0.35f, 0f, m.MarbleDark, false); // цоколь
                    HipRoof(t, min, max, b.h, 0.7f + (float)rnd.NextDouble() * 0.6f, 0.22f, m.Roof);
                    break;
                }
            }
            return t.gameObject;
        }

        /// <summary>Зал: стены по краю пола арены (снаружи на offset), кроме проёмов у дверей, и крыша. Стены — каждая
        /// отдельно (для прозрачности), крыша — одна.</summary>
        public static (List<GameObject> walls, GameObject roof) Hall(Transform parent, Vector2[] floor, IList<Vector2> doors, RoomView.Palette pal)
        {
            var m = M(pal);
            var t = Group(parent, "Hall");
            const float Offset = 0.3f, Thick = 0.5f, Height = 3.4f, DoorClear = 2.2f;
            var outline = Simplify(floor, 0.35f);
            bool ccw = Geo.SignedArea(outline) > 0f;
            var walls = new List<GameObject>();
            for (int i = 0; i < outline.Length; i++)
            {
                var a = outline[i];
                var b = outline[(i + 1) % outline.Length];
                var e = b - a;
                float len = e.magnitude;
                if (len < 0.05f) continue;
                var nrm = ccw ? new Vector2(e.y, -e.x) / len : new Vector2(-e.y, e.x) / len; // наружу
                // кусками по ~1.5 ю: проём у двери вырезает только свои куски
                int pieces = Mathf.Max(1, Mathf.CeilToInt(len / 1.5f));
                for (int k = 0; k < pieces; k++)
                {
                    var p0 = Vector2.Lerp(a, b, k / (float)pieces) + nrm * Offset;
                    var p1 = Vector2.Lerp(a, b, (k + 1f) / pieces) + nrm * Offset;
                    var mid = (p0 + p1) * 0.5f;
                    bool door = false;
                    foreach (var d in doors) if ((d - mid).magnitude < DoorClear) { door = true; break; }
                    if (door) continue;
                    var quad = new[] { p0, p1, p1 + nrm * Thick, p0 + nrm * Thick };
                    var w = Group(t, "Wall");
                    Extrude(w, quad, Height, 0f, m.Marble);
                    Extrude(w, Grow(quad, 0.04f), Height + 0.18f, Height, m.MarbleDark, false);
                    walls.Add(w.gameObject);
                }
            }
            var roof = Group(t, "HallRoof");
            var top = Grow(outline, Offset + Thick + 0.25f);
            Extrude(roof, top, Height + 0.55f, Height + 0.18f, m.Roof, false);
            return (walls, roof.gameObject);
        }

        /// <summary>Упрощение замкнутого контура (Дуглас — Пекер по обеим половинам).</summary>
        static Vector2[] Simplify(Vector2[] p, float tol)
        {
            if (p.Length < 8) return p;
            int half = p.Length / 2;
            var a = new List<Vector2>();
            Rdp(p, 0, half, tol, a);
            a.RemoveAt(a.Count - 1);
            var b = new List<Vector2>();
            var rest = new Vector2[p.Length - half + 1];
            for (int i = 0; i < rest.Length; i++) rest[i] = p[(half + i) % p.Length];
            Rdp(rest, 0, rest.Length - 1, tol, b);
            b.RemoveAt(b.Count - 1);
            a.AddRange(b);
            return a.Count >= 3 ? a.ToArray() : p;
        }

        static void Rdp(Vector2[] p, int i0, int i1, float tol, List<Vector2> o)
        {
            float best = -1f;
            int bi = -1;
            for (int i = i0 + 1; i < i1; i++)
            {
                float d = (Geo.ClosestOnSegment(p[i0], p[i1], p[i]) - p[i]).magnitude;
                if (d > best) { best = d; bi = i; }
            }
            if (best > tol) { Rdp(p, i0, bi, tol, o); o.RemoveAt(o.Count - 1); Rdp(p, bi, i1, tol, o); }
            else { o.Add(p[i0]); o.Add(p[i1]); }
        }
    }
}
