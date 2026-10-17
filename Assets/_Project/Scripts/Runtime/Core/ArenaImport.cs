using System.Collections.Generic;
using Arena.Sim;
using UnityEngine;

namespace Arena.Core
{
    /// <summary>Арена Hades → арена редактора (Arena → Arena Editor → «Импорт арен Тартара»). Как её видит этаж сейчас:
    /// клетка — в форме, если её центр на полу и не в невидимом блокираторе или провале Hades; видимые препятствия
    /// оформляются полисом (PolisDressing.DressArena — слияние мелких и вид по форме) и раскладываются по клеткам
    /// объектами редактора того же вида; шипы и взрывные кубы — ловушками в клетке. Дротики (пластина + излучатель),
    /// урны и детали оформления редактор пока не умеет — они не переносятся.</summary>
    public static class ArenaImport
    {
        public const string Prefix = "t_";
        static readonly Vector2Int[] Four = { Vector2Int.up, Vector2Int.right, Vector2Int.down, Vector2Int.left };

        /// <summary>Чем раскладывать клетки объекта полиса: сначала крупные куски, остаток — мелкими.</summary>
        static string[] PiecesFor(string polis) => polis switch
        {
            PolisDressing.Bench => new[] { "bench", "planter" },
            PolisDressing.Planter => new[] { "planter2", "planter" },
            PolisDressing.Pavilion => new[] { "pavilion", "pedestal", "wall", "column" },
            PolisDressing.Stoa => new[] { "wall", "column" },
            PolisDressing.Statue => new[] { "pedestal", "statue" },
            PolisDressing.Lamp => new[] { "pedestal", "lamp" },
            PolisDressing.Column => new[] { "pedestal", "column" },
            _ => new[] { "pedestal", "wall", "column" },
        };

        public static ArenaLayout FromRoom(RoomDef d, string name)
        {
            var a = new ArenaLayout(name);
            // клетки арены: (0, 0) сетки — мировая точка origin; арена по центру сетки
            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            foreach (var f in d.floor) foreach (var v in f.p) { min = Vector2.Min(min, v); max = Vector2.Max(max, v); }
            var size = max - min;
            var origin = new Vector2(Mathf.Floor(min.x - (a.w - size.x) * 0.5f), Mathf.Floor(min.y - (a.h - size.y) * 0.5f));
            Vector2 Center(int x, int y) => origin + new Vector2(x + 0.5f, y + 0.5f);
            Vector2Int CellOf(Vector2 p) => new(Mathf.FloorToInt(p.x - origin.x), Mathf.FloorToInt(p.y - origin.y));

            bool OnGround(Vector2 p)
            {
                foreach (var f in d.floor) if (f.type != "Lava" && Geo.InPoly(f.p, p)) return true;
                return false;
            }
            bool OnLava(Vector2 p)
            {
                foreach (var h in d.hazards) if (h.type == "Lava" && Geo.InPoly(h.p, p)) return !OnGround(p);
                return false;
            }
            // невидимый блокиратор Hades под клеткой: 1 — держит снаряды (этаж рисует его стеной), 2 — только героя и
            // врагов (бортик); провал — вне арены (-1)
            int Blocker(Vector2 p)
            {
                int k = 0;
                foreach (var w in d.walls)
                {
                    if (w.p.Length < 3 || w.h > 0f && w.type != "Hole" || !Geo.InPoly(w.p, p)) continue;
                    if (w.type == "Hole") return -1;
                    if (w.shots) k = 1;
                    else if (k == 0) k = 2;
                }
                return k;
            }

            // 1. форма: пол Hades без провалов
            var blocker = new int[a.w * a.h];
            for (int y = 0; y < a.h; y++)
                for (int x = 0; x < a.w; x++)
                {
                    var c = Center(x, y);
                    bool floor = false;
                    foreach (var f in d.floor) if (Geo.InPoly(f.p, c)) { floor = true; break; }
                    if (!floor) continue;
                    int b = Blocker(c);
                    if (b < 0) continue;
                    blocker[x + y * a.w] = b;
                    a.SetIn(x, y, true);
                    a.SetSurf(x, y, OnLava(c) ? ArenaSurface.LavaPool.Code : ArenaSurface.Floor.Code);
                }
            // пол Hades вырезан под колоннами и стенами: замкнутые пустоты под ними — тоже арена (там встанут объекты)
            var outside = new bool[a.w * a.h];
            var fill = new Queue<Vector2Int>();
            for (int y = 0; y < a.h; y++)
                for (int x = 0; x < a.w; x++)
                    if ((x == 0 || y == 0 || x == a.w - 1 || y == a.h - 1) && !a.In(x, y)) { outside[x + y * a.w] = true; fill.Enqueue(new Vector2Int(x, y)); }
            while (fill.Count > 0)
            {
                var c = fill.Dequeue();
                foreach (var dv in Four)
                {
                    var n = c + dv;
                    if (!a.Inside(n.x, n.y) || outside[n.x + n.y * a.w] || a.In(n.x, n.y)) continue;
                    outside[n.x + n.y * a.w] = true;
                    fill.Enqueue(n);
                }
            }
            for (int y = 0; y < a.h; y++)
                for (int x = 0; x < a.w; x++)
                {
                    if (a.In(x, y) || outside[x + y * a.w]) continue;
                    var c = Center(x, y);
                    bool covered = false;
                    foreach (var w in d.walls)
                        if (w.p.Length >= 3 && w.type != "Hole" && Geo.InPoly(w.p, c)) { covered = true; break; }
                    if (!covered) continue;
                    blocker[x + y * a.w] = Mathf.Max(0, Blocker(c));
                    a.SetIn(x, y, true);
                    a.SetSurf(x, y, ArenaSurface.Floor.Code);
                }
            Prune(a);

            // 2. ловушки (до препятствий: оболочка слияния их не накрывает, как на этаже)
            var taken = new HashSet<Vector2Int>();
            var keep = new List<Vector2>();
            foreach (var s in d.spawns) keep.Add(s.pos);
            foreach (var t in d.traps)
            {
                keep.Add(t.pos);
                string id = t.type switch { "SpikeTrap" => "spike", "BlastCubeFused" => "blastcube", "BlastCubeFusedRegenerating" => "blastcube_regen", _ => null };
                if (id == null) continue;
                var c = CellOf(t.pos);
                if (!a.IsFloor(c.x, c.y) || !taken.Add(c)) continue;
                a.objects.Add(new ArenaObject { id = id, x = c.x, y = c.y });
            }

            // 3. видимые стены и предметы — объекты полиса, как их оформляет этаж (под ними обычно блокиратор)
            var visible = new List<RoomDef.Poly>();
            foreach (var w in d.walls)
                if (w.p.Length >= 3 && w.h > 0f && w.type != "Hole")
                    visible.Add(new RoomDef.Poly { p = w.p, type = w.type, h = w.h, shots = w.shots });
            var dressed = PolisDressing.DressArena(visible, false, keep, name.GetHashCode());
            dressed.Sort((p, q) => Mathf.Abs(Geo.SignedArea(q.p)).CompareTo(Mathf.Abs(Geo.SignedArea(p.p)))); // крупные — первыми
            foreach (var w in dressed)
            {
                if (w.h <= 0f || w.type == null || !w.type.StartsWith("Polis/")) continue;
                // клетки объекта: центр внутри многоугольника; мелкий — клетка его середины
                var cells = new HashSet<Vector2Int>();
                var bmin = new Vector2(float.MaxValue, float.MaxValue);
                var bmax = new Vector2(float.MinValue, float.MinValue);
                foreach (var v in w.p) { bmin = Vector2.Min(bmin, v); bmax = Vector2.Max(bmax, v); }
                var c0 = CellOf(bmin);
                var c1 = CellOf(bmax);
                for (int y = c0.y; y <= c1.y; y++)
                    for (int x = c0.x; x <= c1.x; x++)
                        if (Geo.InPoly(w.p, Center(x, y))) cells.Add(new Vector2Int(x, y));
                if (cells.Count == 0) cells.Add(CellOf(Geo.Centroid(w.p)));
                cells.RemoveWhere(c => !a.IsFloor(c.x, c.y) || taken.Contains(c));
                Tile(a, cells, PiecesFor(w.type), taken);
            }

            // 4. остальные блокираторы: у края арены — её край (вне формы), внутри — препятствия (стена или бортик)
            var seen = new bool[a.w * a.h];
            for (int y = 0; y < a.h; y++)
                for (int x = 0; x < a.w; x++)
                {
                    var s0 = new Vector2Int(x, y);
                    if (blocker[x + y * a.w] == 0 || seen[x + y * a.w] || taken.Contains(s0) || !a.In(x, y)) continue;
                    var group = new HashSet<Vector2Int>();
                    bool edge = false, high = false;
                    var q = new Queue<Vector2Int>();
                    q.Enqueue(s0);
                    seen[x + y * a.w] = true;
                    while (q.Count > 0)
                    {
                        var c = q.Dequeue();
                        group.Add(c);
                        high |= blocker[c.x + c.y * a.w] == 1;
                        foreach (var dv in Four)
                        {
                            var n = c + dv;
                            if (!a.In(n.x, n.y)) { edge = true; continue; }
                            if (blocker[n.x + n.y * a.w] == 0 || seen[n.x + n.y * a.w] || taken.Contains(n)) continue;
                            seen[n.x + n.y * a.w] = true;
                            q.Enqueue(n);
                        }
                    }
                    if (edge) foreach (var c in group) { a.SetIn(c.x, c.y, false); a.SetSurf(c.x, c.y, ArenaLayout.NoSurface); }
                    else
                    {
                        group.RemoveWhere(c => !a.IsFloor(c.x, c.y));
                        Tile(a, group, high ? new[] { "pedestal", "wall", "column" } : new[] { "planter2", "bench", "planter" }, taken);
                    }
                }
            Prune(a);
            a.objects.RemoveAll(o => !OnShape(a, o));

            // 5. выходов меньше двух — освободить двери, которым мешают только объекты (сначала где их меньше)
            for (int guard = 0; guard < 8; guard++)
            {
                var rep = ArenaCheck.Check(a);
                if (rep.WorkingExits >= 2) break;
                List<ArenaObject> best = null;
                foreach (var e in rep.Exits)
                {
                    if (e.Working) continue;
                    var zone = Zone(e);
                    if (zone.Exists(z => !a.IsFloor(z.x, z.y))) continue; // мешает край или лава — не наш случай
                    var hit = a.objects.FindAll(o => zone.Exists(z => ArenaLayout.Footprint(o).Contains(z)));
                    if (hit.Count > 0 && (best == null || hit.Count < best.Count)) best = hit;
                }
                if (best == null) break;
                foreach (var o in best) a.objects.Remove(o);
            }
            return a;
        }

        /// <summary>Клетки, которые у двери должны быть свободным полом (как в ArenaCheck).</summary>
        static List<Vector2Int> Zone(ArenaCheck.ExitSpot e)
        {
            var v = EtgData.DirVec(e.Dir);
            var side = new Vector2Int(Mathf.Abs(v.y), Mathf.Abs(v.x));
            var zone = new List<Vector2Int>(e.Lanes) { e.Lanes[0] - side, e.Lanes[^1] + side };
            int n = zone.Count;
            for (int i = 0; i < n; i++) zone.Add(zone[i] - v);
            return zone;
        }

        static bool OnShape(ArenaLayout a, ArenaObject o)
        {
            var r = ArenaLayout.Footprint(o);
            for (int y = r.yMin; y < r.yMax; y++) for (int x = r.xMin; x < r.xMax; x++) if (!a.In(x, y)) return false;
            return true;
        }

        /// <summary>Срезать «усики» шириной в клетку (следы растеризации наклонных краёв) и оставить одну, самую большую область.</summary>
        static void Prune(ArenaLayout a)
        {
            for (bool changed = true; changed;)
            {
                changed = false;
                for (int y = 0; y < a.h; y++)
                    for (int x = 0; x < a.w; x++)
                    {
                        if (!a.In(x, y)) continue;
                        if (!a.In(x - 1, y) && !a.In(x + 1, y) || !a.In(x, y - 1) && !a.In(x, y + 1))
                        {
                            a.SetIn(x, y, false);
                            a.SetSurf(x, y, ArenaLayout.NoSurface);
                            changed = true;
                        }
                    }
            }
            KeepLargest(a);
        }

        /// <summary>Разложить клетки кусками по порядку предпочтения (с поворотом на 90°), каждая клетка — один раз.</summary>
        static void Tile(ArenaLayout a, HashSet<Vector2Int> cells, string[] pieces, HashSet<Vector2Int> taken)
        {
            var order = new List<Vector2Int>(cells);
            order.Sort((p, q) => p.y != q.y ? p.y.CompareTo(q.y) : p.x.CompareTo(q.x));
            foreach (var c in order)
            {
                if (!cells.Contains(c)) continue;
                foreach (var id in pieces)
                {
                    var o = Fit(id, c, cells, 0) ?? Fit(id, c, cells, 1);
                    if (o == null) continue;
                    var r = ArenaLayout.Footprint(o);
                    for (int y = r.yMin; y < r.yMax; y++) for (int x = r.xMin; x < r.xMax; x++) { cells.Remove(new Vector2Int(x, y)); taken.Add(new Vector2Int(x, y)); }
                    a.objects.Add(o);
                    break;
                }
            }
        }

        static ArenaObject Fit(string id, Vector2Int at, HashSet<Vector2Int> cells, int rot)
        {
            var o = new ArenaObject { id = id, x = at.x, y = at.y, rot = rot };
            var r = ArenaLayout.Footprint(o);
            for (int y = r.yMin; y < r.yMax; y++) for (int x = r.xMin; x < r.xMax; x++) if (!cells.Contains(new Vector2Int(x, y))) return null;
            return o;
        }

        static void KeepLargest(ArenaLayout a)
        {
            var comp = new int[a.w * a.h];
            for (int i = 0; i < comp.Length; i++) comp[i] = -1;
            var sizes = new List<int>();
            var q = new Queue<Vector2Int>();
            for (int y = 0; y < a.h; y++)
                for (int x = 0; x < a.w; x++)
                {
                    if (!a.In(x, y) || comp[x + y * a.w] >= 0) continue;
                    int id = sizes.Count, n = 0;
                    comp[x + y * a.w] = id;
                    q.Enqueue(new Vector2Int(x, y));
                    while (q.Count > 0)
                    {
                        var c = q.Dequeue();
                        n++;
                        foreach (var d in new[] { Vector2Int.up, Vector2Int.right, Vector2Int.down, Vector2Int.left })
                        {
                            var p = c + d;
                            if (!a.In(p.x, p.y) || comp[p.x + p.y * a.w] >= 0) continue;
                            comp[p.x + p.y * a.w] = id;
                            q.Enqueue(p);
                        }
                    }
                    sizes.Add(n);
                }
            if (sizes.Count < 2) return;
            int best = sizes.IndexOf(Mathf.Max(sizes.ToArray()));
            for (int y = 0; y < a.h; y++)
                for (int x = 0; x < a.w; x++)
                    if (a.In(x, y) && comp[x + y * a.w] != best) { a.SetIn(x, y, false); a.SetSurf(x, y, ArenaLayout.NoSurface); }
        }
    }
}
