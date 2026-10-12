using System;
using System.Collections.Generic;
using UnityEngine;

namespace Arena.Core
{
    /// <summary>Препятствие арены: многоугольник на полу (против часовой стрелки). Из Hades II — вещь с Points и StopsUnits.</summary>
    [Serializable]
    public sealed class ArenaWall
    {
        public string kind;        // wall, gap, tree, bush, rock, pot, door
        public string source;      // тип вещи в Hades II
        public bool projectiles;   // StopsProjectiles: false — вода, обрыв, низкий бордюр (снаряды пролетают)
        public float height;
        public List<Vector2> points = new();
        [NonSerialized] public Vector2 Min, Max;

        public void ComputeBounds()
        {
            Min = new Vector2(float.MaxValue, float.MaxValue);
            Max = new Vector2(float.MinValue, float.MinValue);
            foreach (var p in points) { Min = Vector2.Min(Min, p); Max = Vector2.Max(Max, p); }
        }

        public bool Contains(Vector2 p)
        {
            if (p.x < Min.x || p.x > Max.x || p.y < Min.y || p.y > Max.y) return false;
            bool inside = false;
            for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
            {
                var a = points[i]; var b = points[j];
                if ((a.y > p.y) != (b.y > p.y) && p.x < (b.x - a.x) * (p.y - a.y) / (b.y - a.y) + a.x) inside = !inside;
            }
            return inside;
        }

        /// <summary>Ближайшая точка контура и наружная нормаль ребра.</summary>
        public Vector2 Nearest(Vector2 p, out Vector2 normal)
        {
            float best = float.MaxValue;
            var res = p; normal = Vector2.up;
            for (int i = 0, j = points.Count - 1; i < points.Count; j = i++)
            {
                var a = points[j]; var b = points[i];
                var ab = b - a;
                float l2 = ab.sqrMagnitude;
                float t = l2 > 0f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / l2) : 0f;
                var q = a + ab * t;
                float d = (p - q).sqrMagnitude;
                if (d < best) { best = d; res = q; normal = l2 > 0f ? new Vector2(ab.y, -ab.x).normalized : Vector2.up; }
            }
            return res;
        }

        /// <summary>Круг задевает многоугольник.</summary>
        public bool Overlaps(Vector2 p, float r)
        {
            if (p.x < Min.x - r || p.x > Max.x + r || p.y < Min.y - r || p.y > Max.y + r) return false;
            return Contains(p) || (Nearest(p, out _) - p).sqrMagnitude < r * r;
        }
    }

    /// <summary>Арена: проходимая область, препятствия, старт Ace и точки спавна врагов.
    /// Конфиги сделаны из боевых комнат Hades II (Tools/HadesArenas/convert.py) и лежат в Resources/Arenas.</summary>
    [Serializable]
    public sealed class ArenaLayout
    {
        public string id;
        public string title;
        public string biome;
        public string source;
        public float unitsPerPixel;
        public Vector2 boundsMin, boundsMax;
        public Vector2 playerStart;
        public Vector2 exit;
        public List<Vector2> spawns = new();
        public Vector2 floorOrigin;          // левый нижний угол сетки пола, клетка 1×1
        public List<string> floor = new();   // строки по z снизу вверх: '.' — пол, '#' — нет
        public List<ArenaWall> walls = new();

        public static ArenaLayout FromJson(string json)
        {
            var a = JsonUtility.FromJson<ArenaLayout>(json);
            foreach (var w in a.walls) w.ComputeBounds();
            return a;
        }

        public bool IsFloorCell(int col, int row) =>
            row >= 0 && row < floor.Count && col >= 0 && col < floor[row].Length && floor[row][col] == '.';
    }

    [Serializable]
    public sealed class ArenaIndex
    {
        public string active;
        public List<string> arenas = new();
    }

    /// <summary>Загрузка арен из Resources/Arenas: index.json (список и подключённая арена) и &lt;id&gt;.json.</summary>
    public static class ArenaCatalog
    {
        public const string Folder = "Arenas/";

        public static ArenaIndex Index()
        {
            var t = Resources.Load<TextAsset>(Folder + "index");
            return t != null ? JsonUtility.FromJson<ArenaIndex>(t.text) : new ArenaIndex();
        }

        public static ArenaLayout Load(string id)
        {
            var t = Resources.Load<TextAsset>(Folder + id);
            return t != null ? ArenaLayout.FromJson(t.text) : null;
        }

        /// <summary>Подключённая арена (index.active); null — классическая прямоугольная комната.</summary>
        public static ArenaLayout LoadActive()
        {
            var id = Index().active;
            return string.IsNullOrEmpty(id) ? null : Load(id);
        }
    }
}
