using System;
using System.Collections.Generic;
using UnityEngine;

namespace Arena.Core
{
    /// <summary>Клетка сплошного этажа: 1 клетка Enter the Gungeon = 1 юнит.</summary>
    public enum EtgCell : byte { Void, Floor, Wall, Pit, Door }

    public enum EtgRoomKind { Entrance, Exit, Normal, Hub, Connector, BossFoyer, Boss, Reward }

    /// <summary>Дверь комнаты: клетки выхода на краю комнаты, направление наружу, комната на том конце коридора.</summary>
    public sealed class EtgDoor
    {
        public Vector2Int[] Cells;
        public int Dir, To, Exit;
        public bool Loop, OneWay;
    }

    /// <summary>Комната на этаже: шаблон, клетка (0, 0) шаблона в сетке этажа, двери.</summary>
    public sealed class EtgPlacedRoom
    {
        public int Index;
        public string NodeId;
        public EtgData.Room Room;
        public EtgRoomKind Kind;
        public Vector2Int Pos;
        public readonly List<EtgDoor> Doors = new();
        public bool[] ExitUsed;
        /// <summary>Этаж из арен Hades: арена этой комнаты (null — комната Enter the Gungeon).</summary>
        public ArenaTemplate Arena;
        /// <summary>Этаж из арен: этот хаб без боя (стартовый — ближайший ко входу; остальные хабы — с боем, ArenaFloorBuilder).</summary>
        public bool PeacefulHubs;
        /// <summary>Этаж из арен: 1 — мини-босс, 2 — босс (красный враг с увеличенным здоровьем).</summary>
        public int Elite;
        /// <summary>Этаж из арен: арена в помещении — стены по краю пола и крыша, которая пропадает, когда Смерть внутри.</summary>
        public bool Indoor;
        /// <summary>Этаж из арен: точки появления врагов арены в координатах этажа (ArenaFloorBuilder).</summary>
        public RoomDef.Point[] ArenaSpawns;

        public RectInt Rect => new(Pos.x, Pos.y, Room.w, Room.h);
        /// <summary>Бой при входе: обычные комнаты, хабы (кроме этажа из арен) и босс.</summary>
        public bool Combat => Kind is EtgRoomKind.Normal or EtgRoomKind.Boss or EtgRoomKind.Connector || Kind == EtgRoomKind.Hub && !PeacefulHubs;
        /// <summary>Смещение арены: точка арены q стоит в мире в q + ArenaOffset.</summary>
        public Vector2 ArenaOffset => Arena == null ? Vector2.zero : new Vector2(Pos.x, Pos.y) - Arena.Origin;
        public Vector2 Center => new(Pos.x + Room.w * 0.5f, Pos.y + Room.h * 0.5f);
    }

    /// <summary>Сплошной этаж: сетка клеток в Bounds, комнаты, коридоры между выходами.</summary>
    public sealed class EtgFloorLayout
    {
        public string FloorId, Flow;
        public int Seed, Attempts, LoopsBuilt, LoopsSkipped;
        public RectInt Bounds;
        public EtgCell[] Cells;
        public short[] RoomOf;   // индекс комнаты, −1 — коридор или пусто
        public readonly List<EtgPlacedRoom> Rooms = new();
        public int Entrance = -1, Boss = -1, Exit = -1;
        /// <summary>Этаж из арен Hades (ArenaFloorBuilder): пол и стены — многоугольниками в Geometry, клетки — для раскладки,
        /// мини-карты и комнаты под героем.</summary>
        public RoomDef Geometry;
        public bool Polygonal => Geometry != null;

        public bool InBounds(int x, int y) => x >= Bounds.xMin && y >= Bounds.yMin && x < Bounds.xMax && y < Bounds.yMax;
        int Index(int x, int y) => (x - Bounds.x) + (y - Bounds.y) * Bounds.width;
        public EtgCell At(int x, int y) => InBounds(x, y) ? Cells[Index(x, y)] : EtgCell.Void;
        public int RoomAt(int x, int y) => InBounds(x, y) ? RoomOf[Index(x, y)] : -1;
        public void Set(int x, int y, EtgCell c) { if (InBounds(x, y)) Cells[Index(x, y)] = c; }
        public static bool Walkable(EtgCell c) => c is EtgCell.Floor or EtgCell.Door;
    }

    /// <summary>Сборка этажа по данным Enter the Gungeon:
    /// 1) схема этажа — случайная из его DungeonFlow; узлы с nodeExpands разворачиваются в цепочку обычных комнат
    ///    (minChainLength…maxChainLength); магазины и NPC пока пропускаются;
    /// 2) комната узла — точная (overrideExactRoom), из своей таблицы (overrideRoomTable), для босса — из BossTable этажа,
    ///    иначе из таблицы этажа (fallbackRoomTable) по категории; веса, предел копий, хватает ли выходов;
    /// 3) раскладка от входа: ребёнок встаёт напротив свободного выхода родителя, выход в выход, прямой коридор 2–7 клеток,
    ///    комнаты и коридоры не касаются друг друга (зазор в клетку под стену);
    /// 4) петли (loopTargetNodeGuid) — коридор поиском пути между свободными выходами; не вышло — петля пропускается;
    /// 5) стены — пустые клетки рядом с полом; проверка: от входа пешком доступны все комнаты.
    /// Не получилось — новая попытка с другим зерном (в игре MAX_GENERATION_ATTEMPTS 250). Как Enter the Gungeon
    /// раскладывает этаж в коде, в данных нет — алгоритм раскладки свой.</summary>
    public static class EtgFloorGenerator
    {
        const int Span = 1024, Off = 512;           // рабочее поле раскладки
        static readonly int[] CorridorLengths = { 3, 4, 5, 2, 6, 7 };
        /// <summary>Почему не собралась последняя попытка (для сообщения об ошибке и отладки).</summary>
        public static string LastFailure;
        /// <summary>Сколько попыток на чём сорвалось (отладка генератора).</summary>
        public static readonly Dictionary<string, int> FailureStats = new();
        /// <summary>Последний собранный, но отклонённый этаж (нет связности) — для отладки.</summary>
        public static EtgFloorLayout LastRejected;

        sealed class G
        {
            public string Id;
            public EtgRoomKind Kind;
            public int Cat;
            public string ExactRoom, Table;
            public G Parent;
            public readonly List<G> Children = new();
            public readonly List<(G to, bool oneWay)> Loops = new();
            public EtgPlacedRoom Placed;
        }

        /// <summary>arenas — этаж из арен Hades: обычные комнаты и босс — арены (ArenaSet), остальные — комнаты ETG.</summary>
        public static EtgFloorLayout Generate(string floorId, int seed, int maxAttempts = 200, ArenaSet arenas = null)
        {
            var floor = EtgData.GetFloor(floorId) ?? throw new ArgumentException("Нет этажа " + floorId);
            arenas?.Register();
            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                var rng = new System.Random(unchecked(seed * 486187739 + attempt * 16777619 + 7));
                var flowName = floor.flows[rng.Next(floor.flows.Length)];
                var layout = TryBuild(floor, EtgData.Flows[flowName], rng, arenas);
                if (layout == null)
                {
                    var key = LastFailure ?? "?";
                    if (key.Contains("Boss")) key = key.Substring(key.IndexOf("(", StringComparison.Ordinal) + 1);
                    FailureStats[key] = FailureStats.TryGetValue(key, out int n) ? n + 1 : 1;
                    continue;
                }
                layout.FloorId = floorId; layout.Flow = flowName; layout.Seed = seed; layout.Attempts = attempt + 1;
                return layout;
            }
            throw new InvalidOperationException($"Этаж {floorId}: не собрался за {maxAttempts} попыток; последняя причина: {LastFailure}");
        }

        // ---------- схема ----------

        static G BuildGraph(EtgData.Flow flow, System.Random rng)
        {
            var byId = new Dictionary<string, G>();
            var parentOf = new Dictionary<string, string>();
            foreach (var n in flow.nodes)
                foreach (var c in n.children) parentOf[c] = n.id;
            foreach (var n in flow.nodes)
            {
                if (n.chance < 1f && rng.NextDouble() > n.chance) continue;
                var kind = KindOf(n);
                if (kind == null) continue; // магазин, NPC, кузница — пока без них
                byId[n.id] = new G { Id = n.id, Kind = kind.Value, Cat = n.cat, ExactRoom = n.room, Table = n.table };
            }
            G root = null;
            foreach (var n in flow.nodes)
            {
                if (!byId.TryGetValue(n.id, out var g)) continue;
                // родитель — ближайший сохранённый предок (пропущенные узлы выпадают из цепочки)
                var p = parentOf.TryGetValue(n.id, out var pid) ? pid : null;
                while (p != null && !byId.ContainsKey(p)) p = parentOf.TryGetValue(p, out var pp) ? pp : null;
                if (p == null) { if (root == null || g.Kind == EtgRoomKind.Entrance) root = g; continue; }
                g.Parent = byId[p];
                g.Parent.Children.Add(g);
            }
            foreach (var n in flow.nodes)
                if (!string.IsNullOrEmpty(n.loop) && byId.TryGetValue(n.id, out var a) && byId.TryGetValue(n.loop, out var b))
                    a.Loops.Add((b, n.loopOneWay));
            // nodeExpands: узел — цепочка обычных комнат
            foreach (var n in flow.nodes)
            {
                if (!n.expands || !byId.TryGetValue(n.id, out var g)) continue;
                int extra = Mathf.Max(0, rng.Next(n.chainMin, n.chainMax + 1) - 1);
                var tail = g;
                var kids = new List<G>(g.Children);
                g.Children.Clear();
                for (int i = 0; i < extra; i++)
                {
                    var c = new G { Id = g.Id + "_" + (i + 1), Kind = EtgRoomKind.Normal, Cat = EtgData.Normal, Parent = tail };
                    tail.Children.Add(c);
                    tail = c;
                }
                foreach (var k in kids) { k.Parent = tail; tail.Children.Add(k); }
            }
            return root;
        }

        static EtgRoomKind? KindOf(EtgData.Node n)
        {
            var room = n.room ?? "";
            var table = n.table ?? "";
            if (room.Contains("Entrance")) return EtgRoomKind.Entrance;
            if (room.Contains("Exit")) return EtgRoomKind.Exit;
            switch (n.cat)
            {
                case EtgData.Boss: return EtgRoomKind.Boss;
                case EtgData.Reward: return EtgRoomKind.Reward;
                case EtgData.Hub: return EtgRoomKind.Hub;
                case EtgData.Normal: return EtgRoomKind.Normal;
                case EtgData.Special: return table == "Boss Foyers" ? EtgRoomKind.BossFoyer : null;
                case EtgData.Secret: return null;
                default: return string.IsNullOrEmpty(room) ? EtgRoomKind.Connector : null;
            }
        }

        // ---------- выбор комнаты ----------

        static List<EtgData.TableRoom> PoolOf(G g, EtgData.Floor floor, EtgData.Flow flow, System.Random rng, ArenaSet arenas)
        {
            if (arenas != null && g.Kind == EtgRoomKind.Normal) return arenas.Normal;
            if (arenas != null && g.Kind == EtgRoomKind.Boss) return arenas.Boss;
            if (!string.IsNullOrEmpty(g.ExactRoom))
                return EtgData.Rooms.ContainsKey(g.ExactRoom) ? new List<EtgData.TableRoom> { new() { room = g.ExactRoom } } : new List<EtgData.TableRoom>();
            if (!string.IsNullOrEmpty(g.Table)) return EtgData.Expand(g.Table);
            switch (g.Kind)
            {
                case EtgRoomKind.Boss: return EtgData.Expand(floor.bossTables[rng.Next(floor.bossTables.Length)]);
                case EtgRoomKind.Reward: return EtgData.Expand("Rewards");
            }
            int cat = g.Kind switch { EtgRoomKind.Hub => EtgData.Hub, EtgRoomKind.Connector => EtgData.Connector, _ => EtgData.Normal };
            var pool = EtgData.Expand(flow.fallbackTable, r => r.cat == cat);
            return pool.Count > 0 ? pool : EtgData.Expand(flow.fallbackTable, r => r.cat == EtgData.Normal);
        }

        static EtgData.Room Pick(List<EtgData.TableRoom> pool, int exitsNeeded, Dictionary<string, int> used, System.Random rng, HashSet<string> tried)
        {
            float total = 0f;
            var ok = new List<(EtgData.Room room, float w)>();
            foreach (var t in pool)
            {
                var r = EtgData.Rooms[t.room];
                if (r.exits.Length < exitsNeeded || tried.Contains(r.name)) continue;
                used.TryGetValue(r.name, out int n);
                if (n > 0 && pool.Count > 1) continue;   // на этаже без повторов, если есть из чего выбрать
                ok.Add((r, Mathf.Max(0.01f, t.weight)));
                total += Mathf.Max(0.01f, t.weight);
            }
            if (ok.Count == 0) return null;
            double x = rng.NextDouble() * total;
            foreach (var (room, w) in ok) { x -= w; if (x <= 0) return room; }
            return ok[^1].room;
        }

        // ---------- раскладка ----------

        sealed class Space
        {
            public readonly short[] Occ = new short[Span * Span]; // 0 — свободно, >0 — комната + 1, −1 — коридор
            public readonly List<Vector2Int> Corridors = new();
            public bool Free(int x, int y) => (uint)(x + Off) < Span && (uint)(y + Off) < Span && Occ[(x + Off) + (y + Off) * Span] == 0;
            public short Get(int x, int y) => (uint)(x + Off) < Span && (uint)(y + Off) < Span ? Occ[(x + Off) + (y + Off) * Span] : (short)-2;
            public void Mark(int x, int y, short v) => Occ[(x + Off) + (y + Off) * Span] = v;
        }

        static Vector2Int[] ExitCells(EtgPlacedRoom r, int exit)
        {
            var cs = r.Room.exits[exit].cells;
            var list = new Vector2Int[cs.Length];
            for (int i = 0; i < cs.Length; i++) list[i] = new Vector2Int(r.Pos.x + cs[i].x, r.Pos.y + cs[i].y);
            Array.Sort(list, (a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
            return list;
        }

        /// <summary>Сколько шагов от клетки выхода до первой клетки за прямоугольником комнаты: прямоугольник шаблона включает
        /// собственные стены комнаты, выход — крайняя клетка пола, проход сквозь стену прорубается при сборке.</summary>
        static int Depth(EtgData.Room room, EtgData.RoomExit e)
        {
            var c = e.cells[0];
            return e.d switch { EtgData.West => c.x + 1, EtgData.East => room.w - c.x, EtgData.South => c.y + 1, _ => room.h - c.y };
        }

        /// <summary>Проём двери: клетки выхода, сдвинутые к краю прямоугольника комнаты.</summary>
        static Vector2Int[] DoorCells(EtgPlacedRoom r, int exit, int width)
        {
            var e = r.Room.exits[exit];
            var lanes = ExitCells(r, exit);
            var v = EtgData.DirVec(e.d) * (Depth(r.Room, e) - 1);
            var cells = new Vector2Int[Mathf.Min(width, lanes.Length)];
            for (int i = 0; i < cells.Length; i++) cells[i] = lanes[i] + v;
            return cells;
        }

        static bool RoomFits(Space s, EtgData.Room room, Vector2Int pos)
        {
            for (int y = -1; y <= room.h; y++)
                for (int x = -1; x <= room.w; x++)
                    if (!s.Free(pos.x + x, pos.y + y)) return false;
            return true;
        }

        static void MarkRoom(Space s, EtgPlacedRoom r)
        {
            for (int y = 0; y < r.Room.h; y++)
                for (int x = 0; x < r.Room.w; x++)
                    s.Mark(r.Pos.x + x, r.Pos.y + y, (short)(r.Index + 1));
        }

        /// <summary>Прямой коридор длиной len от клеток выхода наружу: свободен вместе с боковым зазором.</summary>
        static bool CorridorFits(Space s, Vector2Int[] lanes, int dir, int len)
        {
            var v = EtgData.DirVec(dir);
            var side = new Vector2Int(v.y, v.x);
            for (int k = 1; k <= len; k++)
                foreach (var c in lanes)
                {
                    var p = c + v * k;
                    if (!s.Free(p.x, p.y) || !s.Free(p.x + side.x, p.y + side.y) || !s.Free(p.x - side.x, p.y - side.y)) return false;
                }
            return true;
        }

        static void MarkCorridor(Space s, Vector2Int[] lanes, int dir, int len)
        {
            var v = EtgData.DirVec(dir);
            for (int k = 1; k <= len; k++)
                foreach (var c in lanes)
                {
                    var p = c + v * k;
                    s.Mark(p.x, p.y, -1);
                    s.Corridors.Add(p);
                }
        }

        static void Shuffle<T>(IList<T> list, System.Random rng)
        {
            for (int i = list.Count - 1; i > 0; i--) { int j = rng.Next(i + 1); (list[i], list[j]) = (list[j], list[i]); }
        }

        static EtgFloorLayout TryBuild(EtgData.Floor floor, EtgData.Flow flow, System.Random rng, ArenaSet arenas)
        {
            var root = BuildGraph(flow, rng);
            if (root == null) { LastFailure = flow.name + ": нет корня схемы"; return null; }
            var layout = new EtgFloorLayout();
            var space = new Space();
            var used = new Dictionary<string, int>();

            EtgPlacedRoom Place(G g, EtgData.Room room, Vector2Int pos)
            {
                var r = new EtgPlacedRoom
                {
                    Index = layout.Rooms.Count, NodeId = g.Id, Room = room, Kind = g.Kind, Pos = pos, ExitUsed = new bool[room.exits.Length],
                    Arena = arenas?.Get(room.name), PeacefulHubs = arenas != null,
                };
                layout.Rooms.Add(r);
                MarkRoom(space, r);
                used[room.name] = used.TryGetValue(room.name, out int n) ? n + 1 : 1;
                g.Placed = r;
                return r;
            }

            int Needed(G g) => g.Children.Count + (g.Parent != null ? 1 : 0);

            var rootRoom = Pick(PoolOf(root, floor, flow, rng, arenas), Needed(root), used, rng, new HashSet<string>());
            if (rootRoom == null) { LastFailure = flow.name + ": нет комнаты входа " + root.ExactRoom; return null; }
            Place(root, rootRoom, Vector2Int.zero);

            var queue = new Queue<G>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                var p = queue.Dequeue();
                foreach (var c in p.Children)
                {
                    if (!Attach(p.Placed, c)) { LastFailure ??= ""; LastFailure = $"{flow.name}: не встала {c.Kind} {c.Id} к {p.Placed.Room.name} ({LastFailure})"; return null; }
                    queue.Enqueue(c);
                }
            }

            bool Attach(EtgPlacedRoom parent, G child)
            {
                if (TryAttach(parent, child, PoolOf(child, floor, flow, rng, arenas))) return true;
                // этаж из арен: арена не встала (мало выходов, не те стороны) — боевая комната ETG
                return arenas != null && child.Kind is EtgRoomKind.Normal or EtgRoomKind.Boss
                    && TryAttach(parent, child, EtgData.Expand(flow.fallbackTable, r => r.cat == EtgData.Normal));
            }

            bool TryAttach(EtgPlacedRoom parent, G child, List<EtgData.TableRoom> pool)
            {
                if (Pick(pool, Needed(child), used, rng, new HashSet<string>()) == null && string.IsNullOrEmpty(child.ExactRoom))
                    pool = EtgData.Expand(flow.fallbackTable, r => r.cat == EtgData.Normal); // нет подходящей по выходам — обычная
                var tried = new HashSet<string>();
                int widthMiss = 0, corrFail = 0, roomFail = 0, pairs = 0;
                for (int pick = 0; pick < 8; pick++)
                {
                    var room = Pick(pool, Needed(child), used, rng, tried);
                    if (room == null) { LastFailure = $"пул {pool.Count}, нужно выходов {Needed(child)}, проб {pick}; пар {pairs}, ширина {widthMiss}, коридор {corrFail}, место {roomFail}; свободных выходов у родителя {System.Linq.Enumerable.Count(parent.ExitUsed, u => !u)}"; return false; }
                    tried.Add(room.name);
                    var pExits = new List<int>();
                    for (int i = 0; i < parent.Room.exits.Length; i++) if (!parent.ExitUsed[i]) pExits.Add(i);
                    Shuffle(pExits, rng);
                    var cExits = new List<int>();
                    for (int i = 0; i < room.exits.Length; i++) cExits.Add(i);
                    Shuffle(cExits, rng);
                    var lengths = new List<int>(CorridorLengths);
                    Shuffle(lengths, rng);
                    foreach (int pe in pExits)
                    {
                        var pEx = parent.Room.exits[pe];
                        int dir = pEx.d;
                        var v = EtgData.DirVec(dir);
                        foreach (int ce in cExits)
                        {
                            var ex = room.exits[ce];
                            if (ex.d != EtgData.Opposite(dir)) continue;
                            int width = Mathf.Min(ex.cells.Length, pEx.cells.Length); // разная ширина — коридор по узкому
                            if (ex.cells.Length != pEx.cells.Length) widthMiss++;
                            pairs++;
                            var pDoor = DoorCells(parent, pe, width);
                            var cLocal = new Vector2Int[ex.cells.Length];
                            for (int i = 0; i < cLocal.Length; i++) cLocal[i] = new Vector2Int(ex.cells[i].x, ex.cells[i].y);
                            Array.Sort(cLocal, (a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
                            int cDepth = Depth(room, ex);
                            if (!LeavesExitsFor(child, room, ce)) continue;
                            foreach (int len in lengths)
                            {
                                if (!CorridorFits(space, pDoor, dir, len)) { corrFail++; continue; }
                                // проём родителя → коридор len клеток → проём ребёнка (на cDepth−1 от его клетки выхода)
                                var pos = pDoor[0] + v * (len + cDepth) - cLocal[0];
                                if (!RoomFits(space, room, pos)) { roomFail++; continue; }
                                MarkCorridor(space, pDoor, dir, len);
                                var r = Place(child, room, pos);
                                parent.ExitUsed[pe] = true;
                                r.ExitUsed[ce] = true;
                                parent.Doors.Add(new EtgDoor { Cells = pDoor, Dir = dir, To = r.Index, Exit = pe });
                                r.Doors.Add(new EtgDoor { Cells = DoorCells(r, ce, width), Dir = ex.d, To = parent.Index, Exit = ce });
                                return true;
                            }
                        }
                    }
                }
                return false;
            }

            // петли
            var all = new List<G>();
            void Collect(G g) { all.Add(g); foreach (var c in g.Children) Collect(c); }
            Collect(root);
            foreach (var g in all)
                foreach (var (to, oneWay) in g.Loops)
                {
                    if (g.Placed == null || to.Placed == null) continue;
                    if (ConnectLoop(space, g.Placed, to.Placed, oneWay, rng)) layout.LoopsBuilt++;
                    else layout.LoopsSkipped++;
                }

            Rasterize(layout, space);
            foreach (var r in layout.Rooms)
            {
                if (r.Kind == EtgRoomKind.Entrance && layout.Entrance < 0) layout.Entrance = r.Index;
                if (r.Kind == EtgRoomKind.Boss) layout.Boss = r.Index;
                if (r.Kind == EtgRoomKind.Exit) layout.Exit = r.Index;
            }
            if (layout.Entrance < 0) layout.Entrance = 0;
            if (!Connected(layout)) { LastFailure = flow.name + ": не все комнаты доступны от входа"; LastRejected = layout; return null; }
            return layout;
        }

        /// <summary>Вход в комнату через выход ce оставляет каждому ребёнку с точной комнатой (выход с этажа, Драгун)
        /// свободный выход, напротив которого встанет один из его выходов.</summary>
        static bool LeavesExitsFor(G g, EtgData.Room room, int ce)
        {
            foreach (var c in g.Children)
            {
                if (string.IsNullOrEmpty(c.ExactRoom) || !EtgData.Rooms.TryGetValue(c.ExactRoom, out var cr)) continue;
                bool ok = false;
                for (int i = 0; i < room.exits.Length && !ok; i++)
                {
                    if (i == ce) continue;
                    foreach (var e in cr.exits) if (e.d == EtgData.Opposite(room.exits[i].d)) { ok = true; break; }
                }
                if (!ok) return false;
            }
            return true;
        }

        // ---------- петли: поиск пути коридором ширины выхода ----------

        static bool ConnectLoop(Space s, EtgPlacedRoom a, EtgPlacedRoom b, bool oneWay, System.Random rng)
        {
            var aEx = new List<int>();
            var bEx = new List<int>();
            for (int i = 0; i < a.Room.exits.Length; i++) if (!a.ExitUsed[i]) aEx.Add(i);
            for (int i = 0; i < b.Room.exits.Length; i++) if (!b.ExitUsed[i]) bEx.Add(i);
            // сначала пары выходов, смотрящие друг на друга ближе всего
            var pairs = new List<(int ea, int eb, float d)>();
            foreach (int ea in aEx)
                foreach (int eb in bEx)
                {
                    if (a.Room.exits[ea].cells.Length != b.Room.exits[eb].cells.Length && a.Arena == null && b.Arena == null) continue; // арены: мост по узкому
                    var pa = DoorCells(a, ea, 9)[0] + EtgData.DirVec(a.Room.exits[ea].d);
                    var pb = DoorCells(b, eb, 9)[0] + EtgData.DirVec(b.Room.exits[eb].d);
                    pairs.Add((ea, eb, (pa - pb).magnitude));
                }
            pairs.Sort((x, y) => x.d.CompareTo(y.d));
            for (int i = 0; i < pairs.Count && i < 6; i++)
            {
                var (ea, eb, _) = pairs[i];
                int width = Mathf.Min(a.Room.exits[ea].cells.Length, b.Room.exits[eb].cells.Length);
                var path = FindCorridor(s, a, ea, b, eb, width);
                if (path == null) continue;
                foreach (var p in path) { s.Mark(p.x, p.y, -1); s.Corridors.Add(p); }
                a.ExitUsed[ea] = true; b.ExitUsed[eb] = true;
                a.Doors.Add(new EtgDoor { Cells = DoorCells(a, ea, width), Dir = a.Room.exits[ea].d, To = b.Index, Exit = ea, Loop = true, OneWay = oneWay });
                b.Doors.Add(new EtgDoor { Cells = DoorCells(b, eb, width), Dir = b.Room.exits[eb].d, To = a.Index, Exit = eb, Loop = true, OneWay = oneWay });
                return true;
            }
            return false;
        }

        /// <summary>A* квадратом w×w (w — ширина выхода) от клеток за выходом a до клеток за выходом b; квадрат и зазор вокруг него
        /// не задевают ничего, кроме самих комнат a и b у концов.</summary>
        static List<Vector2Int> FindCorridor(Space s, EtgPlacedRoom a, int ea, EtgPlacedRoom b, int eb, int width)
        {
            var la = DoorCells(a, ea, width);
            var lb = DoorCells(b, eb, width);
            int w = la.Length;
            Vector2Int Anchor(Vector2Int[] lanes, int d)
            {
                var v = EtgData.DirVec(d);
                var min = lanes[0] + v;
                if (v.x < 0) min.x -= w - 1;
                if (v.y < 0) min.y -= w - 1;
                return min;
            }
            var start = Anchor(la, a.Room.exits[ea].d);
            var goal = Anchor(lb, b.Room.exits[eb].d);
            short ia = (short)(a.Index + 1), ib = (short)(b.Index + 1);
            bool Ok(Vector2Int p)
            {
                for (int y = -1; y <= w; y++)
                    for (int x = -1; x <= w; x++)
                    {
                        short v = s.Get(p.x + x, p.y + y);
                        bool inner = x >= 0 && y >= 0 && x < w && y < w;
                        if (v == 0) continue;
                        if (!inner && (v == ia || v == ib)) continue; // зазор может касаться своих комнат у концов
                        return false;
                    }
                return true;
            }
            if (!Ok(start) || !Ok(goal)) return null;
            var open = new SortedSet<(int f, int id)>();
            var gScore = new Dictionary<Vector2Int, int> { [start] = 0 };
            var came = new Dictionary<Vector2Int, Vector2Int>();
            var ids = new Dictionary<int, Vector2Int>();
            int H(Vector2Int p) => Mathf.Abs(p.x - goal.x) + Mathf.Abs(p.y - goal.y);
            int next = 0;
            open.Add((H(start), next)); ids[next++] = start;
            int budget = 30000;
            Vector2Int[] steps = { Vector2Int.up, Vector2Int.right, Vector2Int.down, Vector2Int.left };
            while (open.Count > 0 && budget-- > 0)
            {
                var top = open.Min; open.Remove(top);
                var cur = ids[top.id];
                if (cur == goal)
                {
                    var cells = new List<Vector2Int>();
                    for (var p = cur; ; p = came[p])
                    {
                        for (int y = 0; y < w; y++) for (int x = 0; x < w; x++) cells.Add(new Vector2Int(p.x + x, p.y + y));
                        if (p == start) break;
                    }
                    return cells.Count <= 80 * w ? cells : null; // слишком длинная петля — не нужна
                }
                int gc = gScore[cur];
                foreach (var st in steps)
                {
                    var n = cur + st;
                    if (gScore.TryGetValue(n, out int old) && old <= gc + 1) continue;
                    if (!Ok(n)) continue;
                    int turn = came.TryGetValue(cur, out var prev) && cur - prev != st ? 2 : 0; // меньше поворотов
                    gScore[n] = gc + 1 + turn;
                    came[n] = cur;
                    open.Add((gc + 1 + turn + H(n), next)); ids[next++] = n;
                }
            }
            return null;
        }

        // ---------- клетки этажа ----------

        static void Rasterize(EtgFloorLayout layout, Space s)
        {
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            void Grow(int x, int y) { minX = Mathf.Min(minX, x); minY = Mathf.Min(minY, y); maxX = Mathf.Max(maxX, x); maxY = Mathf.Max(maxY, y); }
            foreach (var r in layout.Rooms) { Grow(r.Pos.x, r.Pos.y); Grow(r.Pos.x + r.Room.w - 1, r.Pos.y + r.Room.h - 1); }
            foreach (var c in s.Corridors) Grow(c.x, c.y);
            layout.Bounds = new RectInt(minX - 2, minY - 2, maxX - minX + 5, maxY - minY + 5);
            int n = layout.Bounds.width * layout.Bounds.height;
            layout.Cells = new EtgCell[n];
            layout.RoomOf = new short[n];
            for (int i = 0; i < n; i++) layout.RoomOf[i] = -1;
            foreach (var r in layout.Rooms)
                for (int y = 0; y < r.Room.h; y++)
                    for (int x = 0; x < r.Room.w; x++)
                    {
                        var c = r.Room.At(x, y) switch { '.' => EtgCell.Floor, '#' => EtgCell.Wall, 'o' => EtgCell.Pit, _ => EtgCell.Void };
                        if (c == EtgCell.Void) continue;
                        int gx = r.Pos.x + x, gy = r.Pos.y + y;
                        layout.Set(gx, gy, c);
                        layout.RoomOf[(gx - layout.Bounds.x) + (gy - layout.Bounds.y) * layout.Bounds.width] = (short)r.Index;
                    }
            foreach (var c in s.Corridors) layout.Set(c.x, c.y, EtgCell.Floor);
            foreach (var r in layout.Rooms)
                foreach (var d in r.Doors)
                {
                    // проход сквозь собственную стену комнаты: от клетки выхода до проёма — пол, проём — дверь
                    var back = -EtgData.DirVec(d.Dir);
                    int depth = Depth(r.Room, r.Room.exits[d.Exit]);
                    foreach (var c in d.Cells)
                    {
                        for (int k = 1; k < depth; k++) layout.Set(c.x + back.x * k, c.y + back.y * k, EtgCell.Floor);
                        layout.Set(c.x, c.y, EtgCell.Door);
                    }
                }
            // стены — пустые клетки рядом (в 8 соседях) с полом, дверью или провалом
            var walls = new List<Vector2Int>();
            for (int y = layout.Bounds.yMin; y < layout.Bounds.yMax; y++)
                for (int x = layout.Bounds.xMin; x < layout.Bounds.xMax; x++)
                {
                    if (layout.At(x, y) != EtgCell.Void) continue;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            var c = layout.At(x + dx, y + dy);
                            if (c is EtgCell.Floor or EtgCell.Door or EtgCell.Pit) { walls.Add(new Vector2Int(x, y)); dx = dy = 2; }
                        }
                }
            foreach (var w in walls) layout.Set(w.x, w.y, EtgCell.Wall);
            foreach (var r in layout.Rooms) Bridge(layout, r);
        }

        /// <summary>Внутри комнаты все используемые двери и основная часть пола связаны пешком. В Enter the Gungeon часть
        /// комнат держится на вагонетках и платформах над провалами — у нас их нет, поэтому через провал кладётся мостик
        /// в 3 клетки (у края комнаты — 2), через внутреннюю стену — проход (самый дешёвый путь: пол 1, провал 4, стена 12).</summary>
        static void Bridge(EtgFloorLayout l, EtgPlacedRoom r)
        {
            var rect = r.Rect;
            var comp = new Dictionary<Vector2Int, int>();
            var sizes = new List<int>();
            Vector2Int[] dirs = { Vector2Int.up, Vector2Int.right, Vector2Int.down, Vector2Int.left };
            bool Inside(Vector2Int p) => rect.Contains(p) && l.RoomAt(p.x, p.y) == r.Index;
            void Label()
            {
                comp.Clear(); sizes.Clear();
                for (int y = rect.yMin; y < rect.yMax; y++)
                    for (int x = rect.xMin; x < rect.xMax; x++)
                    {
                        var p0 = new Vector2Int(x, y);
                        if (!Inside(p0) || !EtgFloorLayout.Walkable(l.At(x, y)) || comp.ContainsKey(p0)) continue;
                        int id = sizes.Count, n = 0;
                        var q = new Queue<Vector2Int>();
                        q.Enqueue(p0); comp[p0] = id;
                        while (q.Count > 0)
                        {
                            var p = q.Dequeue(); n++;
                            foreach (var d in dirs)
                            {
                                var nb = p + d;
                                if (!Inside(nb) || !EtgFloorLayout.Walkable(l.At(nb.x, nb.y)) || comp.ContainsKey(nb)) continue;
                                comp[nb] = id; q.Enqueue(nb);
                            }
                        }
                        sizes.Add(n);
                    }
            }
            Label();
            if (sizes.Count <= 1) return;
            int main = 0;
            for (int i = 1; i < sizes.Count; i++) if (sizes[i] > sizes[main]) main = i;
            // каждую дверь — к основной части пола
            foreach (var door in r.Doors)
            {
                Label();
                main = 0;
                for (int i = 1; i < sizes.Count; i++) if (sizes[i] > sizes[main]) main = i;
                var start = door.Cells[0];
                if (!comp.TryGetValue(start, out int c) || c == main) continue;
                // Дейкстра от проёма до любой клетки основной части
                var dist = new Dictionary<Vector2Int, int> { [start] = 0 };
                var prev = new Dictionary<Vector2Int, Vector2Int>();
                var open = new SortedSet<(int d, int x, int y)> { (0, start.x, start.y) };
                Vector2Int? goal = null;
                while (open.Count > 0)
                {
                    var top = open.Min; open.Remove(top);
                    var p = new Vector2Int(top.x, top.y);
                    if (comp.TryGetValue(p, out int pc) && pc == main) { goal = p; break; }
                    foreach (var d in dirs)
                    {
                        var nb = p + d;
                        if (!Inside(nb)) continue;
                        var cell = l.At(nb.x, nb.y);
                        int cost = EtgFloorLayout.Walkable(cell) ? 1 : cell == EtgCell.Pit ? 4 : cell == EtgCell.Wall ? 12 : 30;
                        int nd = top.d + cost;
                        if (dist.TryGetValue(nb, out int od) && od <= nd) continue;
                        dist[nb] = nd; prev[nb] = p;
                        open.Add((nd, nb.x, nb.y));
                    }
                }
                if (goal == null) continue;
                for (var p = goal.Value; p != start; p = prev[p])
                {
                    var step = p - prev[p];
                    var side = new Vector2Int(step.y, step.x);
                    // мостик в 3 клетки: у края комнаты одна из боковых — за стеной, остаётся хотя бы 2
                    foreach (var q in new[] { p, p + side, p - side })
                        if (Inside(q) && !EtgFloorLayout.Walkable(l.At(q.x, q.y))) l.Set(q.x, q.y, EtgCell.Floor);
                }
            }
        }

        /// <summary>От входа пешком (пол и двери) достижима каждая комната.</summary>
        static bool Connected(EtgFloorLayout l)
        {
            var start = FloorCellNear(l, l.Rooms[l.Entrance].Center);
            if (start == null) return false;
            var seen = new HashSet<Vector2Int> { start.Value };
            var q = new Queue<Vector2Int>();
            q.Enqueue(start.Value);
            var reached = new bool[l.Rooms.Count];
            while (q.Count > 0)
            {
                var p = q.Dequeue();
                int r = l.RoomAt(p.x, p.y);
                if (r >= 0) reached[r] = true;
                foreach (var d in new[] { Vector2Int.up, Vector2Int.right, Vector2Int.down, Vector2Int.left })
                {
                    var n = p + d;
                    if (!EtgFloorLayout.Walkable(l.At(n.x, n.y)) || !seen.Add(n)) continue;
                    q.Enqueue(n);
                }
            }
            foreach (bool b in reached) if (!b) return false;
            return true;
        }

        /// <summary>Ближайшая к точке клетка пола (для старта и центра комнаты).</summary>
        public static Vector2Int? FloorCellNear(EtgFloorLayout l, Vector2 p, int radius = 30)
        {
            var c = new Vector2Int(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y));
            for (int r = 0; r <= radius; r++)
                for (int dy = -r; dy <= r; dy++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dy)) != r) continue;
                        var q = new Vector2Int(c.x + dx, c.y + dy);
                        if (l.At(q.x, q.y) == EtgCell.Floor) return q;
                    }
            return null;
        }
    }
}
