using System;
using System.Collections.Generic;
using UnityEngine;

namespace Arena.Core
{
    /// <summary>Проверка арены из редактора, её выходы и клетки появления врагов. Ничего не исправляет и не удаляет —
    /// только сообщает. Ошибки не пускают арену в генерацию (сохранять можно и с ними), предупреждения — нет.
    /// Выходы (двери по 3 клетки, к ним генератор пристыкует мост) — автоматически: на каждой прямой кромке формы от
    /// 5 клеток — по центру, от 12 — два; путь наружу до края сетки свободен. Рабочий выход: клетки двери, их соседи по
    /// краю и ряд перед ней внутрь — свободный пол (не вне формы, не лава, без объектов), и до других рабочих выходов
    /// можно дойти пешком.</summary>
    public static class ArenaCheck
    {
        public const int DoorWidth = 3, MinRun = 5, TwoDoorsRun = 12, MinSpawns = 10, MinPassage = 3;

        public sealed class Issue
        {
            public bool Error;
            public string Text;
            public readonly List<Vector2Int> Cells = new();
        }

        public sealed class ExitSpot
        {
            public int Dir;
            public Vector2Int[] Lanes;
            public bool Working;
            public string Why;
        }

        public sealed class Report
        {
            public readonly List<Issue> Issues = new();
            public readonly List<ExitSpot> Exits = new();
            public readonly List<Vector2Int> Spawns = new();
            public bool Admitted { get { foreach (var i in Issues) if (i.Error) return false; return true; } }
            public int WorkingExits { get { int n = 0; foreach (var e in Exits) if (e.Working) n++; return n; } }
        }

        static readonly Vector2Int[] Four = { Vector2Int.up, Vector2Int.right, Vector2Int.down, Vector2Int.left };
        static readonly int[] Dirs = { EtgData.North, EtgData.East, EtgData.South, EtgData.West };

        /// <summary>Объект какого слоя стоит на клетке (по одному на клетку; при пересечении — последний).</summary>
        static ArenaObject[] Occupancy(ArenaLayout a, out List<Vector2Int> overlaps)
        {
            var occ = new ArenaObject[a.w * a.h];
            overlaps = new List<Vector2Int>();
            foreach (var o in a.objects)
            {
                var r = ArenaLayout.Footprint(o);
                for (int y = r.yMin; y < r.yMax; y++)
                    for (int x = r.xMin; x < r.xMax; x++)
                    {
                        if (!a.Inside(x, y)) continue;
                        if (occ[x + y * a.w] != null) overlaps.Add(new Vector2Int(x, y));
                        occ[x + y * a.w] = o;
                    }
            }
            return occ;
        }

        static bool Blocks(ArenaObject o) => o != null && ArenaPieces.Get(o.id)?.Layer is ArenaLayout.Low or ArenaLayout.High;

        public static Report Check(ArenaLayout a)
        {
            var rep = new Report();
            Issue Add(bool error, string text)
            {
                var i = new Issue { Error = error, Text = text };
                rep.Issues.Add(i);
                return i;
            }
            int W = a.w, H = a.h;
            bool In(int x, int y) => a.In(x, y);

            // форма: не пустая, одна область по 4 соседям
            int shapeCount = a.ShapeCount();
            if (shapeCount == 0) { Add(true, "Форма пустая — отметьте клетки на слое 0 или выберите пресет."); return rep; }
            var comp = Components(W, H, In, out var sizes);
            if (sizes.Count > 1)
            {
                int main = sizes.IndexOf(Mathf.Max(sizes.ToArray()));
                var i = Add(true, $"Форма из {sizes.Count} частей: части должны соединяться стороной клетки (касание углом — не проход).");
                for (int k = 0; k < comp.Length; k++) if (comp[k] >= 0 && comp[k] != main) i.Cells.Add(new Vector2Int(k % W, k / W));
            }

            // поверхность
            var noSurf = new List<Vector2Int>();
            var hiddenSurf = new List<Vector2Int>();
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    char c = a.Surf(x, y);
                    if (In(x, y) && ArenaSurface.Get(c) == null) noSurf.Add(new Vector2Int(x, y));
                    if (!In(x, y) && c != ArenaLayout.NoSurface) hiddenSurf.Add(new Vector2Int(x, y));
                }
            if (noSurf.Count > 0) Add(false, $"Клетки формы без поверхности ({noSurf.Count}) — в игре там будет дыра.").Cells.AddRange(noSurf);
            if (hiddenSurf.Count > 0) Add(false, $"Поверхность за пределами формы ({hiddenSurf.Count} кл.) — скрытые данные, в игру не попадут.").Cells.AddRange(hiddenSurf);

            // объекты: на полу внутри формы, без пересечений
            var occ = Occupancy(a, out var overlaps);
            foreach (var o in a.objects)
            {
                var p = ArenaPieces.Get(o.id);
                var r = ArenaLayout.Footprint(o);
                if (p == null) { Add(true, $"Неизвестный объект «{o.id}» в ({o.x}, {o.y})").Cells.Add(new Vector2Int(o.x, o.y)); continue; }
                bool outside = false, notFloor = false;
                for (int y = r.yMin; y < r.yMax; y++)
                    for (int x = r.xMin; x < r.xMax; x++)
                    {
                        if (!In(x, y)) outside = true;
                        else if (!a.IsFloor(x, y)) notFloor = true;
                    }
                if (outside) AddRect(Add(true, $"{p.Name} ({o.x}, {o.y}) — за пределами формы (скрытые данные)."), r);
                else if (notFloor) AddRect(Add(true, $"{p.Name} ({o.x}, {o.y}) — не на полу (лава или нет поверхности)."), r);
            }
            if (overlaps.Count > 0) Add(true, $"Объекты пересекаются ({overlaps.Count} кл.): одна клетка — один объект.").Cells.AddRange(overlaps);

            // свободный пол — для появления врагов
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    if (a.IsFloor(x, y) && occ[x + y * W] == null) rep.Spawns.Add(new Vector2Int(x, y));
            if (rep.Spawns.Count < MinSpawns) Add(true, $"Мало клеток для появления врагов: {rep.Spawns.Count}, нужно не меньше {MinSpawns} (пол без ловушек и препятствий).");

            Exits(a, occ, rep);
            if (rep.WorkingExits < 2)
            {
                var i = Add(true, $"Рабочих выходов {rep.WorkingExits}, нужно не меньше 2 — освободите пол у кромки формы или удлините её прямые участки (от {MinRun} клеток).");
                foreach (var e in rep.Exits) i.Cells.AddRange(e.Lanes);
            }
            var broken = rep.Exits.FindAll(e => !e.Working);
            if (broken.Count > 0 && rep.WorkingExits >= 2)
            {
                var i = Add(false, $"Нерабочих выходов {broken.Count} из {rep.Exits.Count}: " + string.Join("; ", broken.ConvertAll(e => e.Why)));
                foreach (var e in broken) i.Cells.AddRange(e.Lanes);
            }

            // узкие места формы
            var narrow = new List<Vector2Int>();
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    if (!In(x, y)) continue;
                    int hx = 1, vy = 1;
                    for (int k = x - 1; In(k, y); k--) hx++;
                    for (int k = x + 1; In(k, y); k++) hx++;
                    for (int k = y - 1; In(x, k); k--) vy++;
                    for (int k = y + 1; In(x, k); k++) vy++;
                    if (Mathf.Min(hx, vy) < MinPassage) narrow.Add(new Vector2Int(x, y));
                }
            if (narrow.Count > 0) Add(false, $"Узкие места формы (уже {MinPassage} клеток): {narrow.Count} кл.").Cells.AddRange(narrow);

            // ловушка, закрытая от камеры высоким объектом, на этаж не попадёт (ArenaFloorBuilder)
            foreach (var o in a.objects)
            {
                if (ArenaPieces.Get(o.id)?.Layer != ArenaLayout.Traps) continue;
                if (HiddenTrap(a, o) is string where)
                    Add(false, $"{ArenaPieces.Get(o.id).Name} ({o.x}, {o.y}) закрыта от камеры высоким объектом{where} — на этаж не попадёт.").Cells.Add(new Vector2Int(o.x, o.y));
            }
            return rep;
        }

        static void AddRect(Issue i, RectInt r)
        {
            for (int y = r.yMin; y < r.yMax; y++) for (int x = r.xMin; x < r.xMax; x++) i.Cells.Add(new Vector2Int(x, y));
        }

        static string HiddenTrap(ArenaLayout a, ArenaObject trap)
        {
            bool Hidden(ArenaLayout l, Vector2 p, Func<Vector2, Vector2> map)
            {
                foreach (var o in l.objects)
                {
                    var pc = ArenaPieces.Get(o.id);
                    if (pc == null || pc.Layer != ArenaLayout.High) continue;
                    var r = ArenaLayout.Footprint(o);
                    var c = map(r.center);
                    float rad = Mathf.Sqrt(r.width * r.width + r.height * r.height) * 0.5f;
                    if (PolisDressing.Hides(c, rad, pc.Height, p, 0.1f)) return true;
                }
                return false;
            }
            var at = new Vector2(trap.x + 0.5f, trap.y + 0.5f);
            if (Hidden(a, at, v => v)) return "";
            if (Hidden(a, new Vector2(at.y, at.x), v => new Vector2(v.y, v.x))) return " в отражённой копии";
            return null;
        }

        static int[] Components(int w, int h, Func<int, int, bool> on, out List<int> sizes)
        {
            var comp = new int[w * h];
            for (int i = 0; i < comp.Length; i++) comp[i] = -1;
            sizes = new List<int>();
            var q = new Queue<Vector2Int>();
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (!on(x, y) || comp[x + y * w] >= 0) continue;
                    int id = sizes.Count, n = 0;
                    comp[x + y * w] = id;
                    q.Enqueue(new Vector2Int(x, y));
                    while (q.Count > 0)
                    {
                        var c = q.Dequeue();
                        n++;
                        foreach (var d in Four)
                        {
                            var p = c + d;
                            if (p.x < 0 || p.y < 0 || p.x >= w || p.y >= h || comp[p.x + p.y * w] >= 0 || !on(p.x, p.y)) continue;
                            comp[p.x + p.y * w] = id;
                            q.Enqueue(p);
                        }
                    }
                    sizes.Add(n);
                }
            return comp;
        }

        /// <summary>Кандидаты в выходы по прямым кромкам формы и их пригодность.</summary>
        static void Exits(ArenaLayout a, ArenaObject[] occ, Report rep)
        {
            int W = a.w, H = a.h;
            bool Ground(int x, int y) => a.IsGround(x, y);
            bool Free(int x, int y) => a.IsFloor(x, y) && occ[x + y * W] == null;
            var walk = Components(W, H, (x, y) => a.IsFloor(x, y) && !Blocks(occ[x + y * W]), out _);
            foreach (int dir in Dirs)
            {
                var v = EtgData.DirVec(dir);
                var side = new Vector2Int(Mathf.Abs(v.y), Mathf.Abs(v.x)); // вдоль кромки
                bool Edge(Vector2Int c) => Ground(c.x, c.y) && !Ground(c.x + v.x, c.y + v.y);
                // ряды вдоль кромки: для севера и юга — по y, для востока и запада — по x
                int lines = side.x == 1 ? H : W, len = side.x == 1 ? W : H;
                for (int line = 0; line < lines; line++)
                {
                    Vector2Int At(int k) => side.x == 1 ? new Vector2Int(k, line) : new Vector2Int(line, k);
                    for (int k = 0; k < len;)
                    {
                        if (!Edge(At(k))) { k++; continue; }
                        int start = k;
                        while (k < len && Edge(At(k))) k++;
                        int run = k - start;
                        if (run < MinRun) continue;
                        var centers = run >= TwoDoorsRun ? new[] { start + run / 4, start + run * 3 / 4 } : new[] { start + run / 2 };
                        foreach (int c0 in centers)
                        {
                            int c = Mathf.Clamp(c0, start + 2, start + run - 3);
                            var lanes = new Vector2Int[DoorWidth];
                            for (int i = 0; i < DoorWidth; i++) lanes[i] = At(c - DoorWidth / 2 + i);
                            // путь наружу до края сетки — вне арены (там пройдёт мост)
                            bool open = true;
                            foreach (var l in lanes)
                                for (var p = l + v; a.Inside(p.x, p.y); p += v)
                                    if (Ground(p.x, p.y)) { open = false; break; }
                            if (!open) continue;
                            var spot = new ExitSpot { Dir = dir, Lanes = lanes, Working = true };
                            // дверь и её соседи: края по кромке и ряд перед дверью внутрь — свободный пол
                            var zone = new List<Vector2Int>(lanes) { lanes[0] - side, lanes[DoorWidth - 1] + side };
                            int n = zone.Count;
                            for (int i = 0; i < n; i++) zone.Add(zone[i] - v);
                            foreach (var z in zone)
                                if (!Free(z.x, z.y))
                                {
                                    spot.Working = false;
                                    spot.Why = $"({lanes[1].x}, {lanes[1].y}) — у двери {What(a, occ, z)}";
                                    break;
                                }
                            rep.Exits.Add(spot);
                        }
                    }
                }
            }
            // рабочие выходы должны быть связаны пешком: оставляем самую большую группу
            var groups = new Dictionary<int, int>();
            foreach (var e in rep.Exits)
                if (e.Working) { int g = walk[e.Lanes[1].x + e.Lanes[1].y * W]; groups[g] = groups.TryGetValue(g, out int n) ? n + 1 : 1; }
            int best = -1, bestN = 0;
            foreach (var kv in groups) if (kv.Value > bestN) { best = kv.Key; bestN = kv.Value; }
            foreach (var e in rep.Exits)
                if (e.Working && walk[e.Lanes[1].x + e.Lanes[1].y * W] != best)
                {
                    e.Working = false;
                    e.Why = $"({e.Lanes[1].x}, {e.Lanes[1].y}) — не дойти пешком до других выходов";
                }
        }

        static string What(ArenaLayout a, ArenaObject[] occ, Vector2Int z)
        {
            if (!a.In(z.x, z.y)) return "край формы";
            var o = a.Inside(z.x, z.y) ? occ[z.x + z.y * a.w] : null;
            if (o != null) return ArenaPieces.Get(o.id)?.Name.ToLowerInvariant() ?? o.id;
            var s = ArenaSurface.Get(a.Surf(z.x, z.y));
            return s == null ? "нет поверхности" : s.Name.ToLowerInvariant();
        }

        // ---------- в игру ----------

        /// <summary>Арена как комната (координаты арены: клетка (x, y) — квадрат [x, x+1]×[y, y+1]): пол и лава прямоугольниками,
        /// препятствия — объекты полиса по занимаемой области, ловушки — с параметрами Hades, точки появления — свободный пол.</summary>
        public static RoomDef ToRoom(ArenaLayout a, Report rep)
        {
            int W = a.w, H = a.h;
            var floorMask = new bool[W * H];
            var lavaMask = new bool[W * H];
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    if (a.IsFloor(x, y)) floorMask[x + y * W] = true;
                    else if (a.IsGround(x, y)) lavaMask[x + y * W] = true;
                }
            var floor = new List<RoomDef.Poly>();
            var hazards = new List<RoomDef.Hazard>();
            foreach (var r in Merge(floorMask, W, H)) floor.Add(new RoomDef.Poly { p = Rect(r, 0f), h = 0f });
            foreach (var r in Merge(lavaMask, W, H))
            {
                floor.Add(new RoomDef.Poly { p = Rect(r, 0f), type = "Lava", h = 0f });
                hazards.Add(new RoomDef.Hazard { type = "Lava", p = Rect(r, 0f), damage = ArenaSurface.LavaDamage });
            }
            var walls = new List<RoomDef.Poly>();
            var traps = new List<RoomDef.Trap>();
            foreach (var o in a.objects)
            {
                var p = ArenaPieces.Get(o.id);
                if (p == null) continue;
                var r = ArenaLayout.Footprint(o);
                if (p.Polis != null)
                {
                    walls.Add(new RoomDef.Poly { p = Rect(r, 0.02f), type = p.Polis, h = p.Height, shots = p.Shots });
                    continue;
                }
                var s = ArenaPieces.TrapSample(p.Trap);
                if (s == null) continue;
                bool cube = p.Trap.StartsWith("BlastCube", StringComparison.Ordinal);
                traps.Add(new RoomDef.Trap
                {
                    type = p.Trap, pos = r.center, angle = o.Angle, p = Rect(r, cube ? 0.2f : 0.05f), solid = s.solid, scale = 1f, param = s.param,
                });
            }
            var spawns = new List<RoomDef.Point>();
            var sum = Vector2.zero;
            foreach (var c in rep.Spawns)
            {
                var q = new Vector2(c.x + 0.5f, c.y + 0.5f);
                spawns.Add(new RoomDef.Point { pos = q, role = "" });
                sum += q;
            }
            return new RoomDef
            {
                name = a.name, biome = PolisDressing.Biome, source = "Редактор арен: " + a.name, zoom = 0.95f,
                heroStart = spawns.Count > 0 ? sum / spawns.Count : new Vector2(W * 0.5f, H * 0.5f),
                min = Vector2.zero, max = new Vector2(W, H),
                floor = floor.ToArray(), walls = walls.ToArray(), hazards = hazards.ToArray(), traps = traps.ToArray(), spawns = spawns.ToArray(),
            };
        }

        /// <summary>Внешние контуры клеток пола и лавы (против часовой, без лишних вершин на прямых): по ним встают
        /// стены и крыша зала. Внутренние пустоты не обводятся.</summary>
        public static List<Vector2[]> Outlines(ArenaLayout a)
        {
            // рёбра границы: внутренность слева по ходу
            var edges = new Dictionary<Vector2Int, List<Vector2Int>>();
            void Edge(int x0, int y0, int x1, int y1)
            {
                var s = new Vector2Int(x0, y0);
                if (!edges.TryGetValue(s, out var l)) edges[s] = l = new List<Vector2Int>();
                l.Add(new Vector2Int(x1, y1));
            }
            for (int y = 0; y < a.h; y++)
                for (int x = 0; x < a.w; x++)
                {
                    if (!a.IsGround(x, y)) continue;
                    if (!a.IsGround(x, y - 1)) Edge(x, y, x + 1, y);
                    if (!a.IsGround(x + 1, y)) Edge(x + 1, y, x + 1, y + 1);
                    if (!a.IsGround(x, y + 1)) Edge(x + 1, y + 1, x, y + 1);
                    if (!a.IsGround(x - 1, y)) Edge(x, y + 1, x, y);
                }
            var loops = new List<Vector2[]>();
            while (edges.Count > 0)
            {
                Vector2Int start = default;
                foreach (var k in edges.Keys) { start = k; break; }
                var pts = new List<Vector2Int> { start };
                var cur = start;
                var dir = Vector2Int.zero;
                for (int guard = 0; guard < 100000; guard++)
                {
                    if (!edges.TryGetValue(cur, out var outs) || outs.Count == 0) break;
                    // в точке касания углами — поворот налево: контуры не перескакивают друг в друга
                    int pick = 0;
                    if (outs.Count > 1 && dir != Vector2Int.zero)
                    {
                        var left = new Vector2Int(-dir.y, dir.x);
                        for (int i = 0; i < outs.Count; i++) if (outs[i] - cur == left) pick = i;
                    }
                    var next = outs[pick];
                    outs.RemoveAt(pick);
                    if (outs.Count == 0) edges.Remove(cur);
                    dir = next - cur;
                    cur = next;
                    if (cur == start) break;
                    pts.Add(cur);
                }
                // убрать вершины посреди прямых
                var poly = new List<Vector2>();
                for (int i = 0; i < pts.Count; i++)
                {
                    var p = pts[(i + pts.Count - 1) % pts.Count];
                    var c = pts[i];
                    var n = pts[(i + 1) % pts.Count];
                    var d1 = c - p;
                    var d2 = n - c;
                    if (d1.x * d2.y - d1.y * d2.x != 0) poly.Add(c);
                }
                if (poly.Count >= 3 && Arena.Sim.Geo.SignedArea(poly.ToArray()) > 0f) loops.Add(poly.ToArray());
            }
            return loops;
        }

        const int Pad = 2;

        /// <summary>Шаблон комнаты для генератора этажа: клетки пола и лавы — '.', препятствия — '#', выходы — рабочие.
        /// null — арена не допущена (есть ошибки).</summary>
        public static ArenaTemplate ToTemplate(ArenaLayout a, string name)
        {
            var rep = Check(a);
            if (!rep.Admitted) return null;
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
            for (int y = 0; y < a.h; y++)
                for (int x = 0; x < a.w; x++)
                    if (a.IsGround(x, y)) { x0 = Mathf.Min(x0, x); y0 = Mathf.Min(y0, y); x1 = Mathf.Max(x1, x); y1 = Mathf.Max(y1, y); }
            var occ = Occupancy(a, out _);
            int ox = x0 - Pad, oy = y0 - Pad, gw = x1 - x0 + 1 + Pad * 2, gh = y1 - y0 + 1 + Pad * 2;
            var rows = new string[gh];
            int floorCells = 0;
            var row = new char[gw];
            for (int gy = 0; gy < gh; gy++)
            {
                for (int gx = 0; gx < gw; gx++)
                {
                    int x = gx + ox, y = gy + oy;
                    row[gx] = !a.IsGround(x, y) ? ' ' : Blocks(occ[x + y * a.w]) ? '#' : '.';
                    if (row[gx] == '.') floorCells++;
                }
                rows[gy] = new string(row);
            }
            var exits = new List<EtgData.RoomExit>();
            foreach (var e in rep.Exits)
            {
                if (!e.Working) continue;
                var cells = new EtgData.Cell[e.Lanes.Length];
                for (int i = 0; i < cells.Length; i++) cells[i] = new EtgData.Cell { x = e.Lanes[i].x - ox, y = e.Lanes[i].y - oy };
                exits.Add(new EtgData.RoomExit { d = e.Dir, cells = cells });
            }
            return new ArenaTemplate
            {
                Name = name, Def = ToRoom(a, rep), Origin = new Vector2(ox, oy), FloorCells = floorCells, Layout = a,
                Grid = new EtgData.Room { name = name, cat = EtgData.Normal, w = gw, h = gh, cells = rows, exits = exits.ToArray() },
            };
        }

        static Vector2[] Rect(RectInt r, float inset) => new[]
        {
            new Vector2(r.xMin + inset, r.yMin + inset), new Vector2(r.xMax - inset, r.yMin + inset),
            new Vector2(r.xMax - inset, r.yMax - inset), new Vector2(r.xMin + inset, r.yMax - inset),
        };

        /// <summary>Отмеченные клетки — в прямоугольники (как ArenaFloorBuilder.Merge).</summary>
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
    }
}
