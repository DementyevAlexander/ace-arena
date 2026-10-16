using System.Collections.Generic;
using Arena.Core;
using Arena.Sim;
using UnityEngine;

namespace Arena.View
{
    /// <summary>Полис вокруг боя: кварталы (дома, храмы, кипарисы из RoomDef.city), стены и крыши залов. Всё, что заслоняет
    /// Смерть от камеры (дома, высокие объекты арен, стены залов), становится полупрозрачным; крыша зала пропадает,
    /// пока Смерть в этом зале.</summary>
    public sealed class CityView
    {
        public readonly Transform Root;

        sealed class Fade
        {
            public Renderer[] R;
            public Material[] Opaque;
            public Bounds B;
            public float A = 1f;
            public int Room = -1; // крыша зала: номер комнаты
        }

        readonly List<Fade> fades = new();
        static readonly Dictionary<Material, Material> seeThrough = new();
        readonly MaterialPropertyBlock block = new();
        const float SeeThroughAlpha = 0.28f, FadeSpeed = 5f, Near = 16f;

        public CityView(ArenaSim sim, IEnumerable<GameObject> arenaProps)
        {
            Root = new GameObject("City").transform;
            var pal = RoomView.For(sim.Room.biome);
            var rnd = new System.Random(sim.Room.name?.GetHashCode() ?? 3);
            foreach (var b in sim.Room.city) Add(PolisKit.Building(Root, b, pal, rnd), -1);
            foreach (var go in arenaProps) if (go != null) Add(go, -1);

            // залы: стены по краю арены с проёмами у дверей и крыша
            if (sim.FloorMode && sim.Floor.Polygonal)
                foreach (var r in sim.Floor.Rooms)
                {
                    if (!r.Indoor || r.Arena == null) continue;
                    var doors = new List<Vector2>();
                    foreach (var d in r.Doors)
                    {
                        // середина клеток выхода арены (там мост встаёт к краю её пола)
                        var sum = Vector2.zero;
                        var cells = r.Room.exits[d.Exit].cells;
                        foreach (var c in cells) sum += new Vector2(r.Pos.x + c.x + 0.5f, r.Pos.y + c.y + 0.5f);
                        doors.Add(sum / cells.Length);
                    }
                    var off = r.ArenaOffset;
                    foreach (var f in r.Arena.Def.floor)
                    {
                        var poly = new Vector2[f.p.Length];
                        for (int i = 0; i < poly.Length; i++) poly[i] = f.p[i] + off;
                        var (walls, roof) = PolisKit.Hall(Root, poly, doors, pal);
                        foreach (var w in walls) Add(w, -1);
                        Add(roof, r.Index);
                    }
                }
        }

        void Add(GameObject go, int room)
        {
            var rs = go.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) return;
            var f = new Fade { R = rs, Opaque = new Material[rs.Length], Room = room, B = rs[0].bounds };
            for (int i = 0; i < rs.Length; i++) { f.Opaque[i] = rs[i].sharedMaterial; f.B.Encapsulate(rs[i].bounds); }
            fades.Add(f);
        }

        public void Sync(ArenaSim sim, Camera cam, float dt)
        {
            if (cam == null) return;
            var p3 = Art.W(sim.Player.Pos, 1f);
            var head = cam.WorldToViewportPoint(p3 + Vector3.up * 0.9f);
            var feet = cam.WorldToViewportPoint(p3 - Vector3.up * 0.8f);
            var camPos = cam.transform.position;
            var fwd = cam.transform.forward;
            float playerDepth = Vector3.Dot(p3 - camPos, fwd);
            var cell = new Vector2Int(Mathf.FloorToInt(sim.Player.Pos.x), Mathf.FloorToInt(sim.Player.Pos.y));
            foreach (var f in fades)
            {
                float target = 1f;
                // крыша зала — пока Смерть в его прямоугольнике (уже на подходе по улице) или там идёт бой
                if (f.Room >= 0) target = sim.ActiveRoom == f.Room || sim.FloorMode && sim.Floor.Rooms[f.Room].Rect.Contains(cell) ? 0f : 1f;
                else
                {
                    var c = f.B.center;
                    if (Mathf.Abs(c.x - p3.x) < Near + f.B.extents.x && Mathf.Abs(c.z - p3.z) < Near + f.B.extents.z
                        && Vector3.Dot(f.B.center - camPos, fwd) < playerDepth && Covers(cam, f.B, head, feet))
                        target = SeeThroughAlpha;
                }
                if (f.A == target) continue;
                f.A = Mathf.MoveTowards(f.A, target, FadeSpeed * dt);
                Apply(f);
            }
        }

        /// <summary>Прямоугольник объекта на экране накрывает Смерть (голову или ноги).</summary>
        static bool Covers(Camera cam, Bounds b, Vector3 head, Vector3 feet)
        {
            float x0 = float.MaxValue, x1 = float.MinValue, y0 = float.MaxValue, y1 = float.MinValue;
            var mn = b.min;
            var mx = b.max;
            for (int i = 0; i < 8; i++)
            {
                var v = cam.WorldToViewportPoint(new Vector3((i & 1) == 0 ? mn.x : mx.x, (i & 2) == 0 ? mn.y : mx.y, (i & 4) == 0 ? mn.z : mx.z));
                x0 = Mathf.Min(x0, v.x); x1 = Mathf.Max(x1, v.x); y0 = Mathf.Min(y0, v.y); y1 = Mathf.Max(y1, v.y);
            }
            bool In(Vector3 q) => q.x > x0 && q.x < x1 && q.y > y0 && q.y < y1;
            return In(head) || In(feet);
        }

        void Apply(Fade f)
        {
            bool solid = f.A >= 0.999f;
            for (int i = 0; i < f.R.Length; i++)
            {
                var r = f.R[i];
                if (r == null) continue;
                r.enabled = f.A > 0.01f;
                if (solid)
                {
                    r.sharedMaterial = f.Opaque[i];
                    r.SetPropertyBlock(null);
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                    continue;
                }
                var op = f.Opaque[i];
                if (!seeThrough.TryGetValue(op, out var tm) || tm == null)
                {
                    tm = new Material(op);
                    Art.MakeTransparent(tm, 3000);
                    seeThrough[op] = tm;
                }
                r.sharedMaterial = tm;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                var col = op.GetColor("_BaseColor");
                col.a = f.A;
                block.Clear();
                block.SetColor("_BaseColor", col);
                r.SetPropertyBlock(block);
            }
        }

        public void Destroy() => Object.Destroy(Root.gameObject);
    }
}
