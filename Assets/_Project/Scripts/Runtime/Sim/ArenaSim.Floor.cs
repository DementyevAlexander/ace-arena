using System.Collections.Generic;
using Arena.Core;
using UnityEngine;

namespace Arena.Sim
{
    /// <summary>Сплошной этаж Enter the Gungeon (EtgFloorGenerator): клетки 1×1 юнит — пол, стены, провалы (снаряды
    /// пролетают, пройти нельзя), двери. Вошёл в боевую комнату (не в проёме) — двери запираются и идут волны наших врагов
    /// по набору этажа, все убиты — двери открываются. Комната награды — карточка на земле, босс — карточка после боя.
    /// Комната выхода — шаг в её центр ведёт на следующий этаж: Castle → Gungeon → Mines → Hollow → Forge, дальше по кругу сильнее.
    /// Этаж из арен Hades (StartFloors с arenas): схема ETG, обычные комнаты и босс — арены Тартара, остальное — комнаты ETG,
    /// проходы — мосты над бездной (ArenaFloorBuilder). Столкновения — многоугольниками, как в комнате Hades, двери —
    /// решётки-препятствия. Бой — враги и точки появления самой арены; два мини-босса и босс — красный враг с
    /// увеличенным здоровьем. После каждой боевой комнаты — пятачок: карты, у мини-босса — тринкеты, у босса — тринкеты II.</summary>
    public sealed partial class ArenaSim
    {
        public static readonly string[] FloorOrder = { "Castle", "Gungeon", "Mines", "Hollow", "Forge" };

        public enum FloorRoomState : byte { Unseen, Seen, Visited, Active, Cleared }

        public EtgFloorLayout Floor { get; private set; }
        public bool FloorMode => Floor != null;
        /// <summary>Столкновения по клеткам — этаж Enter the Gungeon; на этаже из арен — многоугольниками, как в комнате Hades.</summary>
        public bool GridCollision => Floor != null && !Floor.Polygonal;
        /// <summary>Этаж из арен Hades: из какого биома арены (пока один — Тартар); сам этаж оформлен полисом.</summary>
        public const string ArenaBiome = "Tartarus";
        ArenaSet floorArenas;
        static ArenaSet arenaCache;
        /// <summary>Забег по этажам из арен Hades.</summary>
        public bool ArenaFloorsOn => floorArenas != null;
        /// <summary>Решётки запертой комнаты этажа из арен — препятствия поперёк её дверей.</summary>
        readonly List<Obstacle> bars = new();
        /// <summary>Забег по этажам: Restart начинает с первого этажа с новым зерном.</summary>
        public bool FloorRun { get; private set; }
        public int FloorIndex { get; private set; }
        /// <summary>Сколько раз пройдены все пять этажей — враги сильнее.</summary>
        public int FloorLap { get; private set; }
        public FloorRoomState[] RoomStates = System.Array.Empty<FloorRoomState>();
        public int ActiveRoom { get; private set; } = -1;
        public int CurrentRoom { get; private set; } = -1;
        /// <summary>Укрытия этажа для Witch и Casket: скопления внутренних стен комнат (центр, радиус).</summary>
        public readonly List<(Vector2 pos, float r)> Covers = new();
        readonly List<RoomDef.Point> roomSpawns = new();

        /// <summary>Наборы наших врагов по этажам и стоимость (как у комнат Hades: Thug 10, Witch 5, Wastrel 13, Casket 9).</summary>
        static readonly string[][] FloorPools =
        {
            new[] { "Thug", "Witch" },
            new[] { "Thug", "Witch", "Casket" },
            new[] { "Wastrel", "Casket", "Thug" },
            new[] { "Wastrel", "Witch", "Casket", "Thug" },
            new[] { "Wastrel", "Witch", "Casket", "Thug" },
        };
        static float Rating(string k) => k switch { "Thug" => 10f, "Witch" => 5f, "Wastrel" => 13f, _ => 9f };

        static readonly System.Collections.Generic.Dictionary<string, string> FloorNames = new()
        {
            ["Castle"] = "Замок Свинцового Лорда", ["Gungeon"] = "Оружейное подземелье", ["Mines"] = "Пороховые шахты",
            ["Hollow"] = "Полость", ["Forge"] = "Кузня",
        };

        /// <summary>Подпись места для HUD, паузы и баннера: этаж и его название, а вне этажа — комната Hades.</summary>
        public string LocationTitle => HubMode ? HubBuilder.Name : FloorMode
            ? $"Этаж {FloorIndex + 1}{(FloorLap > 0 ? $", круг {FloorLap + 1}" : "")} · {FloorName}"
            : Room.Title;

        string FloorName => Floor.Polygonal
            ? (RoomDef.BiomeNames.TryGetValue(Room.biome, out var b) ? b : Room.biome)
            : FloorNames.TryGetValue(Floor.FloorId, out var n) ? n : Floor.FloorId;

        /// <summary>Сколько боевых комнат этажа пройдено и всего.</summary>
        public (int cleared, int total) FloorProgress
        {
            get
            {
                int c = 0, t = 0;
                if (!FloorMode) return (0, 0);
                for (int i = 0; i < Floor.Rooms.Count; i++)
                    if (Floor.Rooms[i].Combat) { t++; if (RoomStates[i] == FloorRoomState.Cleared) c++; }
                return (c, t);
            }
        }

        /// <summary>Начать забег по этажам с первого. arenas — этажи из арен Hades вместо комнат Enter the Gungeon.</summary>
        public void StartFloors(int seed, bool arenas = false)
        {
            FloorRun = true;
            FloorLap = 0;
            floorArenas = arenas ? arenaCache ??= LoadArenas() : null;
            LoadFloor(NewFloor(FloorOrder[0], seed), 0);
        }

        /// <summary>Арены этажа: все комнаты биома и их отражения, плюс арены из редактора (Resources/Arenas, включённые
        /// и без ошибок). Флаг index.json onlyEditor — только арены из редактора (если их нет — как обычно).</summary>
        static ArenaSet LoadArenas()
        {
            var index = ArenaLibrary.LoadIndex();
            var edited = ArenaLibrary.Templates(index);
            var list = new List<ArenaTemplate>();
            if (!index.onlyEditor || edited.Count == 0)
            {
                var defs = new List<RoomDef>();
                foreach (var name in RoomLibrary.All)
                {
                    var d = RoomLibrary.Get(name);
                    if (d != null && d.biome == ArenaBiome) defs.Add(d);
                }
                list.AddRange(ArenaTemplates.Build(defs, ScreenRight));
            }
            list.AddRange(edited);
            return new ArenaSet(list);
        }

        /// <summary>Сбросить кэш арен этажа (редактор арен после сохранения).</summary>
        public static void ReloadArenas() => arenaCache = null;

        /// <summary>Свой набор арен для следующих этажей (тесты); null — снова из ресурсов.</summary>
        public static void UseArenas(ArenaSet set) => arenaCache = set;

        /// <summary>Новый этаж: схема ETG; на этаже из арен — ещё и геометрия многоугольниками.</summary>
        EtgFloorLayout NewFloor(string floorId, int seed)
        {
            var l = EtgFloorGenerator.Generate(floorId, seed, 200, floorArenas);
            if (floorArenas != null) ArenaFloorBuilder.Build(l, PolisDressing.Biome);
            return l;
        }

        /// <summary>Поставить этаж: мир пустеет, герой у входа, состояние героя и скиллов сохраняется.</summary>
        public void LoadFloor(EtgFloorLayout f, int index)
        {
            Floor = f;
            FloorIndex = index;
            var start = EtgFloorGenerator.FloorCellNear(f, f.Rooms[f.Entrance].Center) ?? Vector2Int.zero;
            var b = f.Bounds;
            Room = baseRoom = f.Polygonal ? f.Geometry : new RoomDef
            {
                name = f.Flow, biome = f.FloorId, source = "Enter the Gungeon " + f.Flow, zoom = 1f,
                heroStart = new Vector2(start.x + 0.5f, start.y + 0.5f), heroAngle = 90f,
                min = new Vector2(b.xMin, b.yMin), max = new Vector2(b.xMax, b.yMax),
                encounter = new RoomDef.Encounter { name = "Etg" + f.FloorId, generated = true, budgetPerDepth = 0f },
            };
            var pool = FloorPools[Mathf.Clamp(index, 0, FloorPools.Length - 1)];
            Room.encounter.pool = pool;
            Room.encounter.ratings = System.Array.ConvertAll(pool, Rating);
            bars.Clear();
            if (f.Polygonal) BuildRoom(); // стены, ловушки и урны арен, стены комнат ETG
            else { Obstacles.Clear(); Traps.Clear(); Hazards.Clear(); Breakables.Clear(); doorStand.Clear(); }
            Enemies.Clear(); Effects.Clear(); Bolts.Clear();
            pending.Clear(); plan.Clear(); planIndex = 0;
            megaFight = false; megaera = null; eliteOnly = false;
            ClearLoot();
            PlaceFountain();
            shopStock.Clear(); shopFilled = false;
            ShopRoom = FormsMode ? PickShopRoom(f) : -1;
            DoorsOpen = false;
            if (FormsMode) ResetFormRoom();
            var p = Player;
            p.Pos = Room.heroStart; p.Face = Vector2.up; p.Vel = p.ExtVel = Vector2.zero; p.Stun = 0f;
            p.Cast = null; p.Buffer = -1; p.Lock = null; p.Dashing = false; p.Hidden = 0f; p.ShoeTrail = null;
            RoomStates = new FloorRoomState[f.Rooms.Count];
            ActiveRoom = -1; CurrentRoom = -1;
            if (GridCollision) BuildCovers(); else Covers.Clear();
            Events.Add(new SimEvent { Kind = EventKind.FloorEnter, Value = index });
        }

        /// <summary>Следующий этаж; после Forge — снова Castle, круг сильнее.</summary>
        public void EnterNextFloor()
        {
            int next = FloorIndex + 1;
            if (next >= FloorOrder.Length && ArenaFloorsOn) { WinRun(); return; } // забег из арен — пять этажей, дальше итоги
            if (next >= FloorOrder.Length) { next = 0; FloorLap++; }
            LoadFloor(NewFloor(FloorOrder[next], rng.Next()), next);
        }

        /// <summary>Глубина для пределов живых врагов и бюджета: растёт с этажом и кругом.</summary>
        int FloorDepthLevel => FloorIndex + 2 + FloorLap * 5;

        // ---------- клетки ----------

        /// <summary>Клетка мешает: стена и пустота — всем, провал — только ходьбе, дверь — пока комната заперта.</summary>
        bool CellBlocks(int x, int y, bool shot)
        {
            switch (Floor.At(x, y))
            {
                case EtgCell.Floor: return false;
                case EtgCell.Door: return ActiveRoom >= 0 && Floor.RoomAt(x, y) == ActiveRoom;
                case EtgCell.Pit: return !shot;
                default: return true;
            }
        }

        /// <summary>Круг задевает мешающую клетку.</summary>
        bool GridOverlaps(Vector2 p, float r, bool shot)
        {
            int x0 = Mathf.FloorToInt(p.x - r), x1 = Mathf.FloorToInt(p.x + r), y0 = Mathf.FloorToInt(p.y - r), y1 = Mathf.FloorToInt(p.y + r);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    if (!CellBlocks(x, y, shot)) continue;
                    float cx = Mathf.Clamp(p.x, x, x + 1), cy = Mathf.Clamp(p.y, y, y + 1);
                    float dx = p.x - cx, dy = p.y - cy;
                    if (dx * dx + dy * dy < r * r) return true;
                }
            return false;
        }

        /// <summary>Выталкивает круг из мешающих клеток (до трёх проходов).</summary>
        void GridPush(ref Vector2 p, float r)
        {
            for (int it = 0; it < 3; it++)
            {
                bool moved = false;
                int x0 = Mathf.FloorToInt(p.x - r), x1 = Mathf.FloorToInt(p.x + r), y0 = Mathf.FloorToInt(p.y - r), y1 = Mathf.FloorToInt(p.y + r);
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        if (!CellBlocks(x, y, false)) continue;
                        var c = new Vector2(Mathf.Clamp(p.x, x, x + 1), Mathf.Clamp(p.y, y, y + 1));
                        var d = p - c;
                        float l = d.magnitude;
                        if (l >= r) continue;
                        if (l > 0.0001f) p = c + d / l * r;
                        else
                        {
                            // центр внутри клетки — к ближайшей грани
                            float l0 = p.x - x, l1 = x + 1 - p.x, l2 = p.y - y, l3 = y + 1 - p.y;
                            float m = Mathf.Min(Mathf.Min(l0, l1), Mathf.Min(l2, l3));
                            if (m == l0) p.x = x - r; else if (m == l1) p.x = x + 1 + r; else if (m == l2) p.y = y - r; else p.y = y + 1 + r;
                        }
                        moved = true;
                    }
                if (!moved) return;
            }
        }

        bool GridWalkable(Vector2 p) => !CellBlocks(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y), false) && Floor.At(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y)) != EtgCell.Void;

        bool GridLineOfSight(Vector2 a, Vector2 b, float buffer)
        {
            float len = (b - a).magnitude;
            for (float d = 0f; d <= len; d += 0.25f)
                if (GridOverlaps(Vector2.Lerp(a, b, len > 0f ? d / len : 0f), buffer, true)) return false;
            return true;
        }

        /// <summary>Скопления стен внутри комнат — укрытия для стрелков.</summary>
        void BuildCovers()
        {
            Covers.Clear();
            var f = Floor;
            var seen = new HashSet<Vector2Int>();
            Vector2Int[] dirs = { Vector2Int.up, Vector2Int.right, Vector2Int.down, Vector2Int.left };
            foreach (var r in f.Rooms)
            {
                var rect = r.Rect;
                for (int y = rect.yMin + 1; y < rect.yMax - 1; y++)
                    for (int x = rect.xMin + 1; x < rect.xMax - 1; x++)
                    {
                        var p0 = new Vector2Int(x, y);
                        if (f.At(x, y) != EtgCell.Wall || f.RoomAt(x, y) != r.Index || !seen.Add(p0)) continue;
                        var cells = new List<Vector2Int> { p0 };
                        bool touchesBorder = false;
                        for (int i = 0; i < cells.Count && cells.Count < 60; i++)
                            foreach (var d in dirs)
                            {
                                var n = cells[i] + d;
                                if (f.At(n.x, n.y) != EtgCell.Wall) continue;
                                if (f.RoomAt(n.x, n.y) != r.Index) { touchesBorder = true; continue; }
                                if (seen.Add(n)) cells.Add(n);
                            }
                        if (touchesBorder || cells.Count > 40) continue; // стена комнаты, а не колонна посреди
                        var c = Vector2.zero;
                        foreach (var q in cells) c += new Vector2(q.x + 0.5f, q.y + 0.5f);
                        c /= cells.Count;
                        float rad = 0.5f;
                        foreach (var q in cells) rad = Mathf.Max(rad, (new Vector2(q.x + 0.5f, q.y + 0.5f) - c).magnitude + 0.5f);
                        Covers.Add((c, rad));
                    }
            }
        }

        // ---------- комнаты ----------

        /// <summary>Шаг этажа: какая комната под героем, вход в боевую — запереть и начать бой, награды, выход.</summary>
        void UpdateFloor(float dt)
        {
            var p = Player.Pos;
            int cx = Mathf.FloorToInt(p.x), cy = Mathf.FloorToInt(p.y);
            int r = Floor.RoomAt(cx, cy);
            if (Floor.At(cx, cy) == EtgCell.Door) r = -1; // проём — ещё не в комнате
            if (r != CurrentRoom)
            {
                CurrentRoom = r;
                if (r >= 0) VisitRoom(r);
            }
            if (ActiveRoom < 0 && r >= 0 && RoomStates[r] != FloorRoomState.Cleared && Floor.Rooms[r].Combat && AwayFromDoors(r, p))
                ActivateRoom(r);
            if (ActiveRoom >= 0) UpdateFloorEncounter(dt);
            // выход: центр комнаты выхода
            if (r >= 0 && r == Floor.Exit && ActiveRoom < 0 && (p - Floor.Rooms[r].Center).magnitude < 2.2f && !RewardPending)
                EnterNextFloor();
        }

        bool AwayFromDoors(int r, Vector2 p)
        {
            foreach (var d in Floor.Rooms[r].Doors)
                foreach (var c in d.Cells)
                    if ((new Vector2(c.x + 0.5f, c.y + 0.5f) - p).magnitude < 1.6f + PlayerCfg.radius) return false;
            return true;
        }

        void VisitRoom(int r)
        {
            var room = Floor.Rooms[r];
            if (RoomStates[r] is FloorRoomState.Unseen or FloorRoomState.Seen) RoomStates[r] = FloorRoomState.Visited;
            foreach (var d in room.Doors)
                if (RoomStates[d.To] == FloorRoomState.Unseen) RoomStates[d.To] = FloorRoomState.Seen;
            if (!room.Combat && RoomStates[r] != FloorRoomState.Cleared)
            {
                RoomStates[r] = FloorRoomState.Cleared;
                if (room.Kind == EtgRoomKind.Reward && FormsMode && r != ShopRoom) DropLootAt(room.Center);
            }
            if (r == ShopRoom && FormsMode && !lootShop) OpenShop(); // магазин: прилавок при каждом входе
            Events.Add(new SimEvent { Kind = EventKind.FloorRoomEnter, Value = r });
        }

        void ActivateRoom(int r)
        {
            var room = Floor.Rooms[r];
            ActiveRoom = r;
            RoomStates[r] = FloorRoomState.Active;
            // бюджет волн — по площади пола комнаты и глубине; босс — втрое и три волны
            int cells = 0;
            var rect = room.Rect;
            for (int y = rect.yMin; y < rect.yMax; y++)
                for (int x = rect.xMin; x < rect.xMax; x++)
                    if (Floor.RoomAt(x, y) == r && Floor.At(x, y) == EtgCell.Floor) cells++;
            float area = Mathf.Clamp(cells / 380f, 0.6f, 1.7f);
            bool boss = room.Kind == EtgRoomKind.Boss;
            Room.encounter.budget = (22f + 9f * FloorIndex + 25f * FloorLap) * area * (boss ? 3f : 1f);
            Room.min = new Vector2(rect.xMin, rect.yMin);
            Room.max = new Vector2(rect.xMax, rect.yMax);
            // арена: её набор врагов и точки появления с ролями (melee, ranged)
            var arenaEnc = room.Arena?.Def.encounter;
            if (arenaEnc != null && arenaEnc.pool.Length > 0 && arenaEnc.ratings.Length == arenaEnc.pool.Length)
            {
                Room.encounter.pool = arenaEnc.pool;
                Room.encounter.ratings = arenaEnc.ratings;
            }
            else
            {
                var fp = FloorPools[Mathf.Clamp(FloorIndex, 0, FloorPools.Length - 1)];
                Room.encounter.pool = fp;
                Room.encounter.ratings = System.Array.ConvertAll(fp, Rating);
            }
            roomSpawns.Clear();
            if (room.ArenaSpawns != null)
                foreach (var s in room.ArenaSpawns)
                {
                    if (InsideRoom(s.pos, 0.4f)) roomSpawns.Add(new RoomDef.Point { pos = s.pos, angle = s.angle, role = s.role });
                }
            else
                foreach (var s in room.Room.spawns)
                {
                    var q = new Vector2(room.Pos.x + s.x + 0.5f, room.Pos.y + s.y + 0.5f);
                    if (InsideRoom(q, 0.4f)) roomSpawns.Add(new RoomDef.Point { pos = q, role = "" });
                }
            for (int t = 0; roomSpawns.Count < 10 && t < 400; t++)
            {
                var q = new Vector2(Rand(rect.xMin + 1.5f, rect.xMax - 1.5f), Rand(rect.yMin + 1.5f, rect.yMax - 1.5f));
                if (Floor.RoomAt(Mathf.FloorToInt(q.x), Mathf.FloorToInt(q.y)) == r && InsideRoom(q, 0.6f) && AwayFromDoors(r, q))
                    roomSpawns.Add(new RoomDef.Point { pos = q, role = "" });
            }
            Room.spawns = roomSpawns.ToArray();
            pending.Clear(); plan.Clear(); planIndex = 0;
            Depth = FloorDepthLevel;
            activeCap = 10;
            Wave = 0; // волны считаются заново в каждой комнате
            bool mega = boss && FormsMode; // босс этажа — Мегера: без волн, подмога — только её «Ко мне!»
            eliteOnly = room.Elite == 1 && FormsMode; // мини-босс — без волн, только он (и его дух)
            if (boss && !mega) plan.AddRange(BossPlan());
            waveTimer = 0.6f;
            spawnTimer = 0f;
            if (Floor.Polygonal) LockDoors(room);
            if (mega) StartMegaeraFight(room);
            else if (room.Elite > 0) SpawnElite(room);
            Events.Add(new SimEvent { Kind = EventKind.FloorRoomLocked, Value = r });
        }

        /// <summary>Решётки поперёк дверей комнаты — препятствия, пока она заперта (этаж из арен).</summary>
        void LockDoors(EtgPlacedRoom room)
        {
            foreach (var d in room.Doors)
            {
                var lo = new Vector2(float.MaxValue, float.MaxValue);
                var hi = new Vector2(float.MinValue, float.MinValue);
                foreach (var c in d.Cells) { lo = Vector2.Min(lo, new Vector2(c.x, c.y)); hi = Vector2.Max(hi, new Vector2(c.x + 1, c.y + 1)); }
                var poly = new[] { lo, new Vector2(hi.x, lo.y), hi, new Vector2(lo.x, hi.y) };
                var o = new Obstacle { Pos = (lo + hi) * 0.5f, R = (hi - lo).magnitude * 0.5f, Poly = poly, Height = 1.2f, Type = "Bars", Shots = true };
                bars.Add(o);
                Obstacles.Add(o);
            }
            InvalidateObstacleIndex();
        }

        void UnlockDoors()
        {
            if (bars.Count == 0) return;
            foreach (var o in bars) Obstacles.Remove(o);
            bars.Clear();
            InvalidateObstacleIndex();
        }

        /// <summary>Мини-босс или босс: обычный враг из набора комнаты, красный, здоровье ×eliteHpMini / ×eliteHpBoss.
        /// Встаёт на точку появления дальше всех от Смерти, сразу в бой.</summary>
        void SpawnElite(EtgPlacedRoom room)
        {
            var pool = Room.encounter.pool;
            var kinds = new List<EnemyKind>();
            foreach (var n in pool) if (TryKind(n, out var k)) kinds.Add(k);
            if (kinds.Count == 0) kinds.Add(EnemyKind.Thug);
            var kind = kinds[rng.Next(kinds.Count)];
            var at = room.Center;
            float far = -1f;
            foreach (var s in Room.spawns)
            {
                float d = (s.pos - Player.Pos).sqrMagnitude;
                if (d > far) { far = d; at = s.pos; }
            }
            var e = SpawnEnemy(kind, at);
            e.Elite = true;
            e.MaxHp = e.Hp = e.MaxHp * (room.Elite == 2 ? ArenaCfg.eliteHpBoss : ArenaCfg.eliteHpMini);
            if (IsHades2(kind)) e.SpawnAggro = true;
            if (FormsMode && room.Elite == 1) GiveEliteSkills(e); // мини-босс: два скилла-формы
        }

        /// <summary>Босс: три волны на бюджет комнаты (30% + 30% + 40%).</summary>
        IEnumerable<RoomDef.Wave> BossPlan()
        {
            var enc = Enc;
            float budget = enc.budget + (FormsMode ? Rewards * ArenaCfg.budgetPerReward : 0);
            foreach (float part in new[] { 0.3f, 0.3f, 0.4f })
            {
                var names = new List<string>();
                float slice = budget * part;
                for (int guard = 0; slice > 4f && guard < 40; guard++)
                {
                    int k = rng.Next(enc.pool.Length);
                    names.Add(enc.pool[k]);
                    slice -= enc.ratings[k];
                }
                yield return new RoomDef.Wave { enemies = names.ToArray(), startDelay = 1f };
            }
        }

        /// <summary>Волны запертой комнаты: появление по одному, следующая — когда все прежние убиты; конец — комната пройдена.</summary>
        void UpdateFloorEncounter(float dt)
        {
            if (RewardPending) return;
            if (megaFight) { UpdateMegaeraFight(dt); return; }
            if (eliteOnly) { if (AliveCount() == 0) { eliteOnly = false; ClearRoom(); } return; }
            if (pending.Count > 0)
            {
                spawnTimer -= dt;
                var next = pending.Peek();
                if (spawnTimer > 0f || ActiveWeight() + Weight(next.Kind) > activeCap + 0.001f) return;
                pending.Dequeue();
                var e = SpawnEnemy(next.Kind, PickSpawnPoint(false, next.Kind));
                if (IsHades2(next.Kind)) e.SpawnAggro = true;
                spawnTimer = Rand(intervalMin, intervalMax);
                return;
            }
            if (AliveCount() > 0) return;
            if (planIndex > 0 && planIndex >= plan.Count) { ClearRoom(); return; }
            waveTimer -= dt;
            if (waveTimer > 0f) return;
            StartNextWave();
        }

        void ClearRoom()
        {
            int r = ActiveRoom;
            var room = Floor.Rooms[r];
            RoomStates[r] = FloorRoomState.Cleared;
            ActiveRoom = -1;
            RunRoomsCleared++;
            UnlockDoors();
            var rect = room.Rect;
            Room.min = new Vector2(Floor.Bounds.xMin, Floor.Bounds.yMin);
            Room.max = new Vector2(Floor.Bounds.xMax, Floor.Bounds.yMax);
            pending.Clear(); plan.Clear(); planIndex = 0;
            Events.Add(new SimEvent { Kind = EventKind.FloorRoomCleared, Value = r, Pos = room.Center });
            if (!FormsMode) return;
            // этаж из арен — награда после каждого боя, как в Hades: карты, у мини-босса тринкеты, у босса тринкеты II
            if (Floor.Polygonal)
                RaisePedestal(new Vector2(rect.xMin, rect.yMin), new Vector2(rect.xMax, rect.yMax), null,
                    room.Elite == 1 ? TrinketCfg.miniBossLevel : room.Elite == 2 ? TrinketCfg.bossLevel : 0);
            else if (room.Kind == EtgRoomKind.Boss) DropLootAt(Player.Pos + (room.Center - Player.Pos) * 0.3f);
        }
    }
}
