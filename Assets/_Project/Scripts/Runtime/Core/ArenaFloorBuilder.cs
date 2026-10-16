using System.Collections.Generic;
using Arena.Sim;
using UnityEngine;

namespace Arena.Core
{
    /// <summary>Этаж из арен Hades: раскладка EtgFloorGenerator (арены — шаблоны ArenaTemplate) превращается в одну комнату
    /// многоугольниками (EtgFloorLayout.Geometry). Арены — их пол, стены, ловушки и урны на своих местах. Комнаты Enter the
    /// Gungeon — пол и стены из клеток (соседние клетки слиты в прямоугольники). Проходы между комнатами — мосты над бездной:
    /// пол без стен по бокам. Стены и урны арен, которые перекрывают мост у её двери или выходят за её прямоугольник
    /// (невидимые границы поля Hades), убираются. Ещё здесь выбираются
    /// мини-боссы: две обычные арены дальше всех от входа (сначала тупики). Ловушки, которые на экране закрыты объектами
    /// арены или стеной зала, на этаж не попадают.</summary>
    public static class ArenaFloorBuilder
    {
        public const float TallWall = 1.6f, LowWall = 0.4f;
        const int FrontReach = 3;

        public static void Build(EtgFloorLayout l, string biome)
        {
            var b = l.Bounds;
            var start = EtgFloorGenerator.FloorCellNear(l, l.Rooms[l.Entrance].Center) ?? Vector2Int.zero;
            float zoom = 1f;
            foreach (var r in l.Rooms) if (r.Arena != null) { zoom = r.Arena.Def.zoom; break; }
            var floors = new List<RoomDef.Poly>();
            var walls = new List<RoomDef.Poly>();
            var traps = new List<RoomDef.Trap>();
            var breakables = new List<RoomDef.Prop>();
            var decor = new List<RoomDef.Prop>();

            // клетки мостов и комнат ETG — пол; у арены сюда же клетки её занятых выходов (мост встык с краем пола)
            int w = b.width, h = b.height;
            var walk = new bool[w * h];
            bool IsArena(int room) => room >= 0 && l.Rooms[room].Arena != null;
            for (int y = b.yMin; y < b.yMax; y++)
                for (int x = b.xMin; x < b.xMax; x++)
                    if (EtgFloorLayout.Walkable(l.At(x, y)) && !IsArena(l.RoomAt(x, y)))
                        walk[(x - b.x) + (y - b.y) * w] = true;
            foreach (var r in l.Rooms)
            {
                if (r.Arena == null) continue;
                foreach (var d in r.Doors)
                    foreach (var c in r.Room.exits[d.Exit].cells)
                        walk[(r.Pos.x + c.x - b.x) + (r.Pos.y + c.y - b.y) * w] = true;
            }
            foreach (var rect in Merge(walk, w, h)) floors.Add(RectPoly(rect, b, "Bridge", 0f));

            // стены комнат ETG: дальние от камеры высокие, ближние (за ними пол) — низкие, чтобы не закрывать бой
            var tall = new bool[w * h];
            var low = new bool[w * h];
            for (int y = b.yMin; y < b.yMax; y++)
                for (int x = b.xMin; x < b.xMax; x++)
                {
                    int room = l.RoomAt(x, y);
                    if (l.At(x, y) != EtgCell.Wall || room < 0 || IsArena(room)) continue;
                    (OpensFloor(l, x, y) ? low : tall)[(x - b.x) + (y - b.y) * w] = true;
                }
            foreach (var rect in Merge(tall, w, h)) walls.Add(RectPoly(rect, b, "EtgWall", TallWall));
            foreach (var rect in Merge(low, w, h)) walls.Add(RectPoly(rect, b, "EtgWall", LowWall));

            // кварталы и стены комнат ETG — тоже могут закрыть ловушку на краю арены
            var city = City(l);
            var outerHides = new List<(Vector2 c, float r, float h)>();
            void AddHide(Vector2[] poly, float h)
            {
                if (h <= 0.3f || poly.Length < 3) return;
                var c = Geo.Centroid(poly);
                float rad = 0f;
                foreach (var v in poly) rad = Mathf.Max(rad, (v - c).magnitude);
                outerHides.Add((c, rad, h));
            }
            foreach (var w0 in walls) AddHide(w0.p, w0.h);
            foreach (var c0 in city) AddHide(c0.p, c0.h + 0.8f); // с крышей

            // арены: всё на месте, кроме того, что перекрывает мост; треть — в помещении (босс — всегда)
            var hallRnd = new System.Random(l.Seed * 7919 + 13);
            foreach (var r in l.Rooms)
            {
                if (r.Arena == null) continue;
                r.Indoor = r.Kind == EtgRoomKind.Boss || hallRnd.NextDouble() < 1.0 / 3.0;
                var arenaWalls = new List<RoomDef.Poly>();
                var def = r.Arena.Def;
                var t = r.ArenaOffset;
                var bridge = new List<RectInt>();
                for (int y = r.Pos.y - 1; y <= r.Pos.y + r.Room.h; y++)
                    for (int x = r.Pos.x - 1; x <= r.Pos.x + r.Room.w; x++)
                        if (x >= b.xMin && y >= b.yMin && x < b.xMax && y < b.yMax && walk[(x - b.x) + (y - b.y) * w])
                            bridge.Add(new RectInt(x, y, 1, 1));
                bool OnBridge(Vector2[] poly)
                {
                    foreach (var c in bridge) if (PolyTouchesCell(poly, c)) return true;
                    return false;
                }
                bool PointOnBridge(Vector2 p, float rad)
                {
                    foreach (var c in bridge)
                        if (p.x + rad > c.xMin && p.x - rad < c.xMax && p.y + rad > c.yMin && p.y - rad < c.yMax) return true;
                    return false;
                }
                foreach (var f in def.floor) floors.Add(new RoomDef.Poly { p = Move(f.p, t), type = f.type, h = f.h, shots = f.shots });
                var area = r.Rect;
                bool OutsideArena(Vector2[] poly)
                {
                    foreach (var v in poly)
                        if (v.x < area.xMin - 0.5f || v.y < area.yMin - 0.5f || v.x > area.xMax + 0.5f || v.y > area.yMax + 0.5f) return true;
                    return false;
                }
                foreach (var wl in def.walls)
                {
                    var p = Move(wl.p, t);
                    // невидимые границы поля и задники Hades за прямоугольником арены залезли бы на соседей — край пола и так держит
                    if (p.Length >= 3 && (OutsideArena(p) || OnBridge(p))) continue;
                    arenaWalls.Add(new RoomDef.Poly { p = p, type = wl.type, h = wl.h, shots = wl.shots });
                }
                var spawns = new List<RoomDef.Point>();
                foreach (var s in def.spawns) spawns.Add(new RoomDef.Point { pos = s.pos + t, angle = s.angle, role = s.role });
                r.ArenaSpawns = spawns.ToArray();
                // полис: мелкие препятствия слиты, все — городские объекты (оболочка не накрывает точки появления и ловушки)
                var keep = new List<Vector2>();
                foreach (var s in spawns) keep.Add(s.pos);
                foreach (var tr in def.traps) keep.Add(tr.pos + t);
                foreach (var bk in def.breakables) keep.Add(bk.pos + t);
                var dressed = PolisDressing.DressArena(arenaWalls, r.Indoor, keep, l.Seed * 31 + r.Index);
                walls.AddRange(dressed);
                // что закрывает арену от камеры: её объекты и стены зала
                var hides = new List<(Vector2 c, float r, float h)>();
                foreach (var dw in dressed)
                {
                    if (dw.h <= 0.3f || dw.p.Length < 3) continue;
                    var c = Geo.Centroid(dw.p);
                    float rad = 0f;
                    foreach (var v in dw.p) rad = Mathf.Max(rad, (v - c).magnitude);
                    hides.Add((c, rad, dw.h));
                }
                if (r.Indoor)
                    foreach (var f in def.floor)
                        foreach (var (c, rad) in PolisDressing.HallPieces(Move(f.p, t), DoorPoints(r)))
                            hides.Add((c, rad, PolisDressing.HallHeight));
                bool Hidden(Vector2 p)
                {
                    foreach (var (c, rad, hh) in hides) if (PolisDressing.Hides(c, rad, hh, p, 0.1f)) return true;
                    foreach (var (c, rad, hh) in outerHides)
                        if (Mathf.Abs(c.x - p.x) < 14f && Mathf.Abs(c.y - p.y) < 14f && PolisDressing.Hides(c, rad, hh, p, 0.1f)) return true;
                    return false;
                }

                // ловушки — только те, что не на мосту и не закрыты от камеры
                var trapIndex = new Dictionary<int, int>();
                for (int i = 0; i < def.traps.Length; i++)
                {
                    var tr = def.traps[i];
                    if (PointOnBridge(tr.pos + t, 0.6f) || Hidden(tr.pos + t)) continue;
                    trapIndex[i] = traps.Count;
                    traps.Add(new RoomDef.Trap
                    {
                        type = tr.type, pos = tr.pos + t, angle = tr.angle, p = Move(tr.p, t), link = tr.link, chain = tr.chain,
                        solid = tr.solid, scale = tr.scale, param = tr.param,
                    });
                }
                // ссылки ловушек — на новые индексы (пропавшие — без связи)
                for (int i = 0; i < def.traps.Length; i++)
                {
                    if (!trapIndex.TryGetValue(i, out int ni)) continue;
                    var tr = traps[ni];
                    tr.link = tr.link >= 0 && trapIndex.TryGetValue(tr.link, out int nl) ? nl : -1;
                    var chain = new List<int>();
                    foreach (int c in tr.chain ?? System.Array.Empty<int>()) if (trapIndex.TryGetValue(c, out int nc)) chain.Add(nc);
                    tr.chain = chain.ToArray();
                }
                foreach (var p in def.breakables)
                    if (!PointOnBridge(p.pos + t, p.r)) breakables.Add(new RoomDef.Prop { type = p.type, pos = p.pos + t, r = p.r, h = p.h });
                foreach (var p in def.decor) decor.Add(new RoomDef.Prop { type = p.type, pos = p.pos + t, r = p.r, h = p.h });
            }

            l.Geometry = new RoomDef
            {
                name = l.Flow, biome = biome, source = "Этаж из арен Hades: " + l.Flow, zoom = zoom,
                heroStart = new Vector2(start.x + 0.5f, start.y + 0.5f), heroAngle = 90f,
                min = new Vector2(b.xMin, b.yMin), max = new Vector2(b.xMax, b.yMax),
                floor = floors.ToArray(), walls = walls.ToArray(), traps = traps.ToArray(),
                breakables = breakables.ToArray(), decor = decor.ToArray(), city = city.ToArray(),
                encounter = new RoomDef.Encounter { name = "ArenaFloor", generated = true, budgetPerDepth = 0f },
            };
            PickElites(l);
        }

        /// <summary>Середины занятых дверей арены — где мост встаёт к краю её пола (проёмы в стенах зала).</summary>
        public static List<Vector2> DoorPoints(EtgPlacedRoom r)
        {
            var list = new List<Vector2>();
            foreach (var d in r.Doors)
            {
                var sum = Vector2.zero;
                var cells = r.Room.exits[d.Exit].cells;
                foreach (var c in cells) sum += new Vector2(r.Pos.x + c.x + 0.5f, r.Pos.y + c.y + 0.5f);
                list.Add(sum / cells.Length);
            }
            return list;
        }

        /// <summary>Кварталы вокруг проходимого: от мостов и пола комнат ETG — через клетку, от арен (края многоугольниками) —
        /// через две; строить не дальше CityReach клеток от проходимого.</summary>
        const int CityReach = 7;

        static List<RoomDef.Poly> City(EtgFloorLayout l)
        {
            var b = l.Bounds;
            int pad = CityReach + 1;
            int w = b.width + pad * 2, h = b.height + pad * 2;
            var origin = new Vector2(b.xMin - pad, b.yMin - pad);
            // расстояние (по Чебышёву) до проходимого: мосты и комнаты ETG — от клеток, арены — с запасом в клетку
            var dist = new int[w * h];
            for (int i = 0; i < dist.Length; i++) dist[i] = int.MaxValue;
            var q = new Queue<int>();
            for (int y = b.yMin; y < b.yMax; y++)
                for (int x = b.xMin; x < b.xMax; x++)
                {
                    var c = l.At(x, y);
                    int room = l.RoomAt(x, y);
                    bool arena = room >= 0 && l.Rooms[room].Arena != null;
                    // проходимое и провалы, у арены — и её стены с колоннами (иначе дом вырос бы внутри большой стены)
                    if (!EtgFloorLayout.Walkable(c) && c != EtgCell.Pit && !(arena && c == EtgCell.Wall)) continue;
                    int k = (x - b.xMin + pad) + (y - b.yMin + pad) * w;
                    dist[k] = arena ? -1 : 0; // арена — на клетку «ближе»
                    q.Enqueue(k);
                }
            while (q.Count > 0)
            {
                int k = q.Dequeue(), x = k % w, y = k / w;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                        int n = nx + ny * w;
                        if (dist[n] <= dist[k] + 1) continue;
                        dist[n] = dist[k] + 1;
                        q.Enqueue(n);
                    }
            }
            var blocked = new bool[w * h];
            var buildable = new bool[w * h];
            for (int i = 0; i < w * h; i++) { blocked[i] = dist[i] <= 1; buildable[i] = dist[i] <= CityReach; }
            return PolisDressing.Build(blocked, buildable, w, h, origin, l.Seed * 104729 + 3);
        }

        /// <summary>Босс — комната босса; мини-боссы — две обычные арены дальше всех от входа по дверям, сначала тупики.
        /// Хабы — с боем, кроме стартового (ближайшего ко входу).</summary>
        static void PickElites(EtgFloorLayout l)
        {
            var dist = new int[l.Rooms.Count];
            for (int i = 0; i < dist.Length; i++) dist[i] = -1;
            var q = new Queue<int>();
            dist[l.Entrance] = 0; q.Enqueue(l.Entrance);
            while (q.Count > 0)
            {
                int r = q.Dequeue();
                foreach (var d in l.Rooms[r].Doors)
                    if (dist[d.To] < 0) { dist[d.To] = dist[r] + 1; q.Enqueue(d.To); }
            }
            // хабы — с боем, кроме стартового (ближайшего ко входу)
            EtgPlacedRoom startHub = null;
            foreach (var r in l.Rooms)
                if (r.Kind == EtgRoomKind.Hub && dist[r.Index] >= 0 && (startHub == null || dist[r.Index] < dist[startHub.Index])) startHub = r;
            foreach (var r in l.Rooms)
                if (r.Kind == EtgRoomKind.Hub) r.PeacefulHubs = r == startHub;
            var cand = new List<EtgPlacedRoom>();
            foreach (var r in l.Rooms)
            {
                if (r.Kind == EtgRoomKind.Boss) r.Elite = 2;
                else if (r.Kind == EtgRoomKind.Normal && r.Arena != null) cand.Add(r);
            }
            cand.Sort((a, c) =>
            {
                int ea = a.Doors.Count == 1 ? 0 : 1, ec = c.Doors.Count == 1 ? 0 : 1;
                return ea != ec ? ea.CompareTo(ec) : dist[c.Index].CompareTo(dist[a.Index]);
            });
            for (int i = 0; i < cand.Count && i < 2; i++) cand[i].Elite = 1;
        }

        static Vector2[] Move(Vector2[] p, Vector2 t)
        {
            if (p == null) return System.Array.Empty<Vector2>();
            var o = new Vector2[p.Length];
            for (int i = 0; i < p.Length; i++) o[i] = p[i] + t;
            return o;
        }

        static RoomDef.Poly RectPoly(RectInt r, RectInt b, string type, float h)
        {
            float x0 = b.x + r.xMin, y0 = b.y + r.yMin, x1 = b.x + r.xMax, y1 = b.y + r.yMax;
            return new RoomDef.Poly { p = new[] { new Vector2(x0, y0), new Vector2(x1, y0), new Vector2(x1, y1), new Vector2(x0, y1) }, type = type, h = h, shots = true };
        }

        /// <summary>Отмеченные клетки — в прямоугольники: ряды отрезками, одинаковые отрезки соседних рядов сливаются.</summary>
        static List<RectInt> Merge(bool[] m, int w, int h)
        {
            var done = new bool[m.Length];
            var list = new List<RectInt>();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (!m[x + y * w] || done[x + y * w]) continue;
                    int x1 = x;
                    while (x1 + 1 < w && m[x1 + 1 + y * w] && !done[x1 + 1 + y * w]) x1++;
                    int y1 = y;
                    for (bool grow = true; grow && y1 + 1 < h;)
                    {
                        for (int i = x; i <= x1; i++) if (!m[i + (y1 + 1) * w] || done[i + (y1 + 1) * w]) { grow = false; break; }
                        if (grow) y1++;
                    }
                    for (int j = y; j <= y1; j++) for (int i = x; i <= x1; i++) done[i + j * w] = true;
                    list.Add(new RectInt(x, y, x1 - x + 1, y1 - y + 1));
                }
            return list;
        }

        static bool OpensFloor(EtgFloorLayout f, int x, int y)
        {
            for (int j = 0; j <= FrontReach; j++)
                for (int i = 0; i <= FrontReach; i++)
                {
                    if (i == 0 && j == 0) continue;
                    var c = f.At(x + i, y + j);
                    if (EtgFloorLayout.Walkable(c) || c == EtgCell.Pit) return true;
                }
            return false;
        }

        /// <summary>Многоугольник задевает клетку: вершина в клетке, центр или угол клетки в многоугольнике.</summary>
        static bool PolyTouchesCell(Vector2[] poly, RectInt c)
        {
            foreach (var v in poly) if (v.x > c.xMin && v.x < c.xMax && v.y > c.yMin && v.y < c.yMax) return true;
            var pts = new[]
            {
                new Vector2(c.xMin + 0.5f, c.yMin + 0.5f), new Vector2(c.xMin + 0.05f, c.yMin + 0.05f), new Vector2(c.xMax - 0.05f, c.yMin + 0.05f),
                new Vector2(c.xMax - 0.05f, c.yMax - 0.05f), new Vector2(c.xMin + 0.05f, c.yMax - 0.05f),
            };
            foreach (var p in pts) if (Geo.InPoly(poly, p)) return true;
            return false;
        }
    }
}
