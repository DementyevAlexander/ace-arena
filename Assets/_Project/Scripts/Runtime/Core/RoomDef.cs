using System;
using System.Collections.Generic;
using UnityEngine;

namespace Arena.Core
{
    /// <summary>Комната, перенесённая из карты Hades (Content/macOS/Maps/*.thing_bin, конвертер Tools/HadesRooms).
    /// Координаты уже в юнитах арены и в плоскости пола (x, z мира).</summary>
    [Serializable]
    public sealed class RoomDef
    {
        public string name;
        public string biome;
        public string source;
        /// <summary>Масштаб камеры комнаты в Hades (RoomData*.lua: ZoomFraction), 1 — обычный.</summary>
        public float zoom = 1f;
        public Vector2 heroStart;
        public float heroAngle;
        public Vector2 min, max;
        /// <summary>Контуры пола: внутри любого из них можно стоять.</summary>
        public Poly[] floor = Array.Empty<Poly>();
        /// <summary>Сплошные препятствия: стены, колонны, камни.</summary>
        public Poly[] walls = Array.Empty<Poly>();
        /// <summary>Опасные зоны: лава, вода Стикса, пропасти.</summary>
        public Hazard[] hazards = Array.Empty<Hazard>();
        public Point[] spawns = Array.Empty<Point>();
        public Point[] exits = Array.Empty<Point>();
        public Trap[] traps = Array.Empty<Trap>();
        public Prop[] breakables = Array.Empty<Prop>();
        /// <summary>Крупные детали оформления без столкновений (для вида).</summary>
        public Prop[] decor = Array.Empty<Prop>();
        public Encounter encounter;

        [Serializable]
        public sealed class Poly
        {
            public Vector2[] p = Array.Empty<Vector2>();
            public string type;
            /// <summary>Высота при отрисовке (стена, колонна, низкий камень).</summary>
            public float h = 1f;
            /// <summary>StopsProjectiles: false — вода, обрыв, низкий бордюр (ходить нельзя, снаряды пролетают).</summary>
            public bool shots = true;
        }

        [Serializable]
        public sealed class Hazard
        {
            public string type;
            public Vector2[] p = Array.Empty<Vector2>();
            public float damage;
        }

        [Serializable]
        public sealed class Point
        {
            public Vector2 pos;
            public float angle;
            /// <summary>Точка появления: melee, ranged или support (группы SpawnPoint в картах Hades).</summary>
            public string role;
        }

        [Serializable]
        public sealed class Trap
        {
            public string type;
            public Vector2 pos;
            /// <summary>Направление в градусах (atan2 по x, z мира).</summary>
            public float angle;
            /// <summary>Площадка или тело ловушки.</summary>
            public Vector2[] p = Array.Empty<Vector2>();
            /// <summary>Пластина дротиков → индекс излучателя; -1 — нет.</summary>
            public int link = -1;
            /// <summary>Пластина топоров → индексы топоров (RemoteTrapChains).</summary>
            public int[] chain = Array.Empty<int>();
            public bool solid;
            public float scale = 1f;
            public TrapParam param = new();
        }

        /// <summary>Параметры ловушки из данных Hades (EnemyData.lua, EnemyWeapons.sjson): тайминги в секундах,
        /// урон в единицах Hades, расстояния и скорости — уже в юнитах арены.</summary>
        [Serializable]
        public sealed class TrapParam
        {
            public float pre, fire, post, cooldown, cooldownMin, cooldownMax, wakeMin, wakeMax;
            public float trigger, enemyMul = 1f, fuse, dormant;
            public float damage, range, speed, radius, knock, barrel;
        }

        [Serializable]
        public sealed class Prop
        {
            public string type;
            public Vector2 pos;
            public float r;
            public float h;
        }

        [Serializable]
        public sealed class Encounter
        {
            public string name;
            public Wave[] waves = Array.Empty<Wave>();
            /// <summary>После заданных волн — генерация волн по бюджету сложности, как GeneratedTartarus.</summary>
            public bool generated = true;
            public int activeCap = 10;
            /// <summary>Кого генерировать и почём (DifficultyRating). Пусто — враги Тартара: Thug 10, Witch 5.</summary>
            public string[] pool = Array.Empty<string>();
            public float[] ratings = Array.Empty<float>();
            /// <summary>Бюджет сгенерированного набора: budget + budgetPerDepth × глубина (Тартар: 30 + 11 × глубина; Эреб: 55).</summary>
            public float budget = 30f;
            public float budgetPerDepth = 11f;
            /// <summary>Сгенерированный набор — одна волна (Эреб) или 1–3 волны (Тартар).</summary>
            public bool singleWave;
            /// <summary>Следующая волна — через waveDelay, когда живых осталось не больше waveRemaining (Эреб: 0.5 с и 1).</summary>
            public float waveDelay = 1f;
            public int waveRemaining;
        }

        [Serializable]
        public sealed class Wave
        {
            public string[] enemies = Array.Empty<string>();
            public float startDelay = 1f;
            public float intervalMin = 0.175f;
            public float intervalMax = 0.225f;
            /// <summary>Враги волны уже стоят в комнате (PreSpawnEnemies), без руны призыва.</summary>
            public bool preSpawned;
        }

        /// <summary>Копия, отражённая по горизонтали экрана, как Hades отражает комнаты при входе (RoomData: Flipped).
        /// Ось отражения проходит через центр комнаты вдоль направления «вверх» экрана.</summary>
        public RoomDef Mirrored(Vector2 screenRight)
        {
            var c = (min + max) * 0.5f;
            var r = screenRight.normalized;
            Vector2 P(Vector2 v) => v - 2f * Vector2.Dot(v - c, r) * r;
            Vector2 D(Vector2 v) => v - 2f * Vector2.Dot(v, r) * r;
            float A(float deg)
            {
                var d = D(new Vector2(Mathf.Cos(deg * Mathf.Deg2Rad), Mathf.Sin(deg * Mathf.Deg2Rad)));
                return Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
            }
            Vector2[] Ps(Vector2[] a) { var o = new Vector2[a.Length]; for (int i = 0; i < a.Length; i++) o[i] = P(a[i]); return o; }
            Poly[] Polys(Poly[] a) => Array.ConvertAll(a, x => new Poly { p = Ps(x.p), type = x.type, h = x.h, shots = x.shots });
            Point[] Points(Point[] a) => Array.ConvertAll(a, x => new Point { pos = P(x.pos), angle = A(x.angle), role = x.role });
            var m = new RoomDef
            {
                name = name, biome = biome, source = source, zoom = zoom, heroStart = P(heroStart), heroAngle = A(heroAngle),
                floor = Polys(floor), walls = Polys(walls), spawns = Points(spawns), exits = Points(exits), encounter = encounter,
                hazards = Array.ConvertAll(hazards, x => new Hazard { type = x.type, p = Ps(x.p), damage = x.damage }),
                traps = Array.ConvertAll(traps, x => new Trap { type = x.type, pos = P(x.pos), angle = A(x.angle), p = Ps(x.p), link = x.link, chain = x.chain, solid = x.solid, scale = x.scale, param = x.param }),
                breakables = Array.ConvertAll(breakables, x => new Prop { type = x.type, pos = P(x.pos), r = x.r, h = x.h }),
                decor = Array.ConvertAll(decor, x => new Prop { type = x.type, pos = P(x.pos), r = x.r, h = x.h }),
                Flipped = !Flipped,
            };
            var a0 = P(min);
            var a1 = P(max);
            var b0 = P(new Vector2(min.x, max.y));
            var b1 = P(new Vector2(max.x, min.y));
            m.min = Vector2.Min(Vector2.Min(a0, a1), Vector2.Min(b0, b1));
            m.max = Vector2.Max(Vector2.Max(a0, a1), Vector2.Max(b0, b1));
            return m;
        }

        [NonSerialized] public bool Flipped;

        /// <summary>Арена Hades II, из которой сделана комната: по ней рисуется ночной город. null — комната Hades.</summary>
        [NonSerialized] public ArenaLayout Layout;
        public bool IsHades2 => Layout != null;

        /// <summary>Арена Hades II (Resources/Arenas) в формате комнаты: пол — рамка арены, стены — её препятствия,
        /// волны — как бой Эреба (FIntroFight, затем GeneratedF на бюджет 55: Wastrel 18, Casket 12).</summary>
        public static RoomDef FromArena(ArenaLayout a)
        {
            var mn = a.boundsMin;
            var mx = a.boundsMax;
            var r = new RoomDef
            {
                name = a.id, biome = a.biome, source = a.source, zoom = a.zoom > 0f ? a.zoom : 1f,
                heroStart = a.playerStart, min = mn, max = mx, Layout = a,
                floor = new[] { new Poly { type = "Floor", h = 0f, p = new[] { mn, new Vector2(mx.x, mn.y), mx, new Vector2(mn.x, mx.y) } } },
                walls = a.walls.ConvertAll(w => new Poly { p = w.points.ToArray(), type = w.kind, h = w.height, shots = w.projectiles }).ToArray(),
                spawns = a.spawns.ConvertAll(p => new Point { pos = p, role = "" }).ToArray(),
                exits = a.exit != Vector2.zero ? new[] { new Point { pos = a.exit } } : Array.Empty<Point>(),
                encounter = new Encounter
                {
                    name = "FIntroFight", activeCap = 4, generated = true, singleWave = true,
                    pool = new[] { "Wastrel", "Casket" }, ratings = new[] { 18f, 12f }, budget = 55f, budgetPerDepth = 0f,
                    waveDelay = 0.5f, waveRemaining = 1,
                    waves = new[]
                    {
                        new Wave { enemies = new[] { "Wastrel" }, preSpawned = true, intervalMin = 0.2f, intervalMax = 0.4f },
                        new Wave { enemies = new[] { "Casket", "Casket", "Casket" }, intervalMin = 0.2f, intervalMax = 0.4f },
                        new Wave { enemies = new[] { "Wastrel", "Casket" }, intervalMin = 0.2f, intervalMax = 0.4f },
                    },
                },
            };
            var d = (mn + mx) * 0.5f - r.heroStart;
            r.heroAngle = d.sqrMagnitude > 0.01f ? Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg : 90f;
            return r;
        }

        /// <summary>Прямоугольная пустая комната — для тестов и запасного варианта.</summary>
        public static RoomDef Box(float halfWidth, float halfDepth, string name = "Box")
        {
            var r = new RoomDef
            {
                name = name, biome = "Test", min = new Vector2(-halfWidth, -halfDepth), max = new Vector2(halfWidth, halfDepth),
                floor = new[] { new Poly { p = new[] { new Vector2(-halfWidth, -halfDepth), new Vector2(halfWidth, -halfDepth), new Vector2(halfWidth, halfDepth), new Vector2(-halfWidth, halfDepth) } } },
            };
            return r;
        }
    }

    /// <summary>Список комнат из Resources/Rooms. Подключена только первая (index.json → enabled).</summary>
    public static class RoomLibrary
    {
        [Serializable]
        sealed class Index
        {
            public string[] rooms = Array.Empty<string>();
            public string[] enabled = Array.Empty<string>();
        }

        static Index index;

        static Index Load()
        {
            if (index != null) return index;
            var ta = Resources.Load<TextAsset>("Rooms/index");
            index = ta != null ? JsonUtility.FromJson<Index>(ta.text) : new Index();
            return index;
        }

        static List<string> all;

        /// <summary>Все комнаты: 30 из Hades (Resources/Rooms) и 30 арен Hades II (Resources/Arenas).</summary>
        public static IReadOnlyList<string> All
        {
            get
            {
                if (all != null) return all;
                all = new List<string>(Load().rooms);
                all.AddRange(ArenaCatalog.Index().arenas);
                return all;
            }
        }

        public static IReadOnlyList<string> Enabled => Load().enabled;

        public static RoomDef Get(string name)
        {
            var ta = Resources.Load<TextAsset>("Rooms/" + name);
            if (ta != null) return JsonUtility.FromJson<RoomDef>(ta.text);
            var arena = ArenaCatalog.Load(name);
            return arena != null ? RoomDef.FromArena(arena) : null;
        }

        /// <summary>Комната за дверью: случайная из подключённых (сейчас подключена одна — та же комната).</summary>
        public static RoomDef Next(System.Random rng, string current)
        {
            var e = Enabled;
            if (e.Count == 0) return null;
            string pick = e[rng.Next(e.Count)];
            if (e.Count > 1 && pick == current) pick = e[(Array.IndexOf(Load().enabled, pick) + 1) % e.Count];
            return Get(pick);
        }

        /// <summary>Комната для игры: первая подключённая, иначе пустой прямоугольник.</summary>
        public static RoomDef Default()
        {
            var e = Enabled;
            var r = e.Count > 0 ? Get(e[0]) : null;
            return r ?? RoomDef.Box(10f, 7f);
        }
    }
}
