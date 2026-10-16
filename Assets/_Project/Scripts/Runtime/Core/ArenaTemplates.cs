using System.Collections.Generic;
using Arena.Sim;
using UnityEngine;

namespace Arena.Core
{
    /// <summary>Арена Hades как шаблон комнаты Enter the Gungeon для генератора этажа: клетки 1×1 ю — пол там, где центр клетки
    /// на полу арены ('#' — внутри стены или колонны), выходы — двери арены по сторонам света. Дверей у арены 2–3: вход
    /// (край пола у точки появления героя) и выходы Hades. В бою арена остаётся многоугольниками (Def), клетки нужны
    /// раскладке, мини-карте и тому, в какой комнате стоит Смерть.</summary>
    public sealed class ArenaTemplate
    {
        public string Name;
        /// <summary>Арена (возможно, отражённая) в своих координатах.</summary>
        public RoomDef Def;
        /// <summary>Мировая точка арены, на которую приходится клетка (0, 0) шаблона.</summary>
        public Vector2 Origin;
        public EtgData.Room Grid;
        /// <summary>Площадь пола в клетках — для выбора арены босса.</summary>
        public int FloorCells;
    }

    /// <summary>Арены для генератора этажа: пул обычных комнат и пул босса (самые большие арены).</summary>
    public sealed class ArenaSet
    {
        public readonly List<ArenaTemplate> All;
        public readonly List<EtgData.TableRoom> Normal = new(), Boss = new();
        readonly Dictionary<string, ArenaTemplate> byName = new();

        public ArenaSet(List<ArenaTemplate> all, int bossCount = 4)
        {
            All = all;
            foreach (var t in all) { byName[t.Name] = t; Normal.Add(new EtgData.TableRoom { room = t.Name, weight = 1f }); }
            var big = new List<ArenaTemplate>(all);
            big.Sort((a, b) => b.FloorCells.CompareTo(a.FloorCells));
            for (int i = 0; i < big.Count && i < bossCount; i++) Boss.Add(new EtgData.TableRoom { room = big[i].Name, weight = 1f });
        }

        public ArenaTemplate Get(string name) => name != null && byName.TryGetValue(name, out var t) ? t : null;

        /// <summary>Шаблоны — в общий список комнат ETG: генератор выбирает комнаты по имени.</summary>
        public void Register()
        {
            foreach (var t in All) EtgData.Rooms[t.Name] = t.Grid;
        }
    }

    public static class ArenaTemplates
    {
        const int Pad = 2;       // запас клеток вокруг пола арены
        const int DoorWidth = 3; // ширина выхода (моста) в клетках

        static readonly int[] Dirs = { EtgData.North, EtgData.East, EtgData.South, EtgData.West };

        /// <summary>Шаблоны арен; mirrorAxis — ещё и отражённые по этой оси копии (горизонталь экрана, как Hades отражает
        /// комнаты при входе), null — без них.</summary>
        public static List<ArenaTemplate> Build(IEnumerable<RoomDef> rooms, Vector2? mirrorAxis)
        {
            var list = new List<ArenaTemplate>();
            foreach (var r in rooms)
            {
                if (r == null || r.floor.Length == 0) continue;
                var t = Convert(r, "Hades/" + r.name);
                if (t != null) list.Add(t);
                if (mirrorAxis == null) continue;
                var m = Convert(r.Mirrored(mirrorAxis.Value), "Hades/" + r.name + "/m");
                if (m != null) list.Add(m);
            }
            return list;
        }

        static bool OnFloor(RoomDef d, Vector2 p)
        {
            foreach (var f in d.floor) if (Geo.InPoly(f.p, p)) return true;
            return false;
        }

        static bool InWall(RoomDef d, Vector2 p)
        {
            foreach (var w in d.walls) if (w.p.Length >= 3 && w.h > 0f && Geo.InPoly(w.p, p)) return true;
            return false;
        }

        /// <summary>Ближайшая точка края пола и направление наружу.</summary>
        static (Vector2 at, Vector2 outward) EdgeNear(RoomDef d, Vector2 p)
        {
            Vector2 best = p;
            float bd = float.MaxValue;
            foreach (var f in d.floor)
            {
                var q = Geo.ClosestOnEdge(f.p, p, out float dist);
                if (dist < bd) { bd = dist; best = q; }
            }
            // точка на полу — наружу к краю; за краем (двери Hades стоят в стене, чуть за полом) — от края к ней
            var n = OnFloor(d, p) ? best - p : p - best;
            if (n.sqrMagnitude < 1e-6f) n = p - d.heroStart;
            return (best, n.sqrMagnitude > 1e-6f ? n.normalized : Vector2.down);
        }

        static ArenaTemplate Convert(RoomDef d, string name)
        {
            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            foreach (var f in d.floor) foreach (var v in f.p) { min = Vector2.Min(min, v); max = Vector2.Max(max, v); }
            var origin = new Vector2(Mathf.Floor(min.x) - Pad, Mathf.Floor(min.y) - Pad);
            int w = Mathf.CeilToInt(max.x - origin.x) + Pad, h = Mathf.CeilToInt(max.y - origin.y) + Pad;
            var cells = new char[h][];
            int floorCells = 0;
            for (int y = 0; y < h; y++)
            {
                cells[y] = new char[w];
                for (int x = 0; x < w; x++)
                {
                    var c = origin + new Vector2(x + 0.5f, y + 0.5f);
                    cells[y][x] = !OnFloor(d, c) ? ' ' : InWall(d, c) ? '#' : '.';
                    if (cells[y][x] == '.') floorCells++;
                }
            }
            bool Inside(int x, int y) => x >= 0 && y >= 0 && x < w && y < h;

            // двери: вход у точки появления героя и выходы Hades
            var doors = new List<(Vector2 at, Vector2 outward)> { EdgeNear(d, d.heroStart) };
            foreach (var e in d.exits) doors.Add(EdgeNear(d, e.pos));
            var exits = new List<EtgData.RoomExit>();
            foreach (var (at, outward) in doors)
            {
                // сторона света: смотрит наружу и до края шаблона идёт по пустоте, а не поперёк пола
                int bestDir = -1;
                float bestScore = float.MaxValue;
                foreach (int dir in Dirs)
                {
                    var v = EtgData.DirVec(dir);
                    float dot = Vector2.Dot(outward, new Vector2(v.x, v.y));
                    if (dot < 0.2f) continue;
                    var b = Cell(at - new Vector2(v.x, v.y) * 0.6f, origin);
                    int crossed = 0, len = 0;
                    foreach (var lane in Lanes(b, dir))
                        for (var p = lane + v; Inside(p.x, p.y); p += v, len++)
                            if (cells[p.y][p.x] != ' ') crossed++;
                    float score = crossed * 10f + len * 0.05f - dot * 3f;
                    if (score < bestScore) { bestScore = score; bestDir = dir; }
                }
                if (bestDir < 0) continue;
                var vv = EtgData.DirVec(bestDir);
                var baseCell = Cell(at - new Vector2(vv.x, vv.y) * 0.6f, origin);
                var lanes = Lanes(baseCell, bestDir);
                bool ok = true;
                foreach (var l in lanes) if (!Inside(l.x, l.y) || !Inside(l.x + vv.x, l.y + vv.y)) ok = false;
                if (!ok) continue;
                var exitCells = new EtgData.Cell[lanes.Length];
                for (int i = 0; i < lanes.Length; i++)
                {
                    var l = lanes[i];
                    cells[l.y][l.x] = '.';
                    for (var p = l + vv; Inside(p.x, p.y); p += vv) cells[p.y][p.x] = ' '; // путь наружу — пустота (там пройдёт мост)
                    exitCells[i] = new EtgData.Cell { x = l.x, y = l.y };
                }
                exits.Add(new EtgData.RoomExit { d = bestDir, cells = exitCells });
            }
            if (exits.Count < 2) return null; // без входа и выхода арена этажу не годится

            var rows = new string[h];
            for (int y = 0; y < h; y++) rows[y] = new string(cells[y]);
            return new ArenaTemplate
            {
                Name = name, Def = d, Origin = origin, FloorCells = floorCells,
                Grid = new EtgData.Room { name = name, cat = EtgData.Normal, w = w, h = h, cells = rows, exits = exits.ToArray() },
            };
        }

        static Vector2Int Cell(Vector2 p, Vector2 origin) => new(Mathf.FloorToInt(p.x - origin.x), Mathf.FloorToInt(p.y - origin.y));

        /// <summary>Клетки выхода поперёк направления: DoorWidth штук с центром в base.</summary>
        static Vector2Int[] Lanes(Vector2Int b, int dir)
        {
            var v = EtgData.DirVec(dir);
            var side = new Vector2Int(v.y, v.x);
            var lanes = new Vector2Int[DoorWidth];
            for (int i = 0; i < DoorWidth; i++) lanes[i] = b + side * (i - DoorWidth / 2);
            return lanes;
        }
    }
}
