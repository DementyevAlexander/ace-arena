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

        public static IReadOnlyList<string> All => Load().rooms;
        public static IReadOnlyList<string> Enabled => Load().enabled;

        public static RoomDef Get(string name)
        {
            var ta = Resources.Load<TextAsset>("Rooms/" + name);
            return ta == null ? null : JsonUtility.FromJson<RoomDef>(ta.text);
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
