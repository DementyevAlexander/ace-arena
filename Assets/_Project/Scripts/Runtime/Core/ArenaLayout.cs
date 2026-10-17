using System;
using System.Collections.Generic;
using UnityEngine;

namespace Arena.Core
{
    /// <summary>Арена из редактора (Arena → Arena Editor): внутреннее пространство комнаты на клетках 1×1 ю, без стен, дверей
    /// и крыши. Пять логических слоёв: 0 — форма, 1 — поверхность, 2 — ловушки, 3 — низкие препятствия, 4 — высокие.
    /// Слои хранятся раздельно: поверхность и объекты остаются и за пределами формы (их показывает проверка, ArenaCheck).
    /// Файл — Resources/Arenas/&lt;имя&gt;.json, ряды снизу вверх (ось y вверх, как у комнат ETG).</summary>
    [Serializable]
    public sealed class ArenaLayout : ISerializationCallbackReceiver
    {
        public const int Size = 32;
        public const int Shape = 0, Surface = 1, Traps = 2, Low = 3, High = 4;
        public static readonly string[] LayerNames = { "0 · Форма", "1 · Поверхность", "2 · Ловушки", "3 · Низкие препятствия", "4 · Высокие препятствия" };
        /// <summary>Открытая или закрытая (стены по краю пола и крыша): auto — как у арен Hades, треть случайно; босс — всегда закрытая.</summary>
        public const string RoofAuto = "auto", RoofOpen = "open", RoofClosed = "closed";
        public const char NoSurface = ' ';

        public string name = "";
        public int w = Size, h = Size;
        /// <summary>Ряды снизу вверх: '#' — клетка арены, '.' — вне её.</summary>
        public string[] shape = Array.Empty<string>();
        /// <summary>Ряды снизу вверх: код поверхности (ArenaSurface.Code), ' ' — нет.</summary>
        public string[] surface = Array.Empty<string>();
        public List<ArenaObject> objects = new();
        public string roof = RoofAuto;

        [NonSerialized] bool[] cells;
        [NonSerialized] char[] surf;

        public ArenaLayout() { }

        public ArenaLayout(string name, int w = Size, int h = Size)
        {
            this.name = name;
            this.w = w;
            this.h = h;
            Ensure();
        }

        void Ensure()
        {
            if (cells != null && cells.Length == w * h) return;
            cells = new bool[w * h];
            surf = new char[w * h];
            for (int i = 0; i < surf.Length; i++) surf[i] = NoSurface;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    if (y < shape.Length && x < shape[y].Length) cells[x + y * w] = shape[y][x] == '#';
                    if (y < surface.Length && x < surface[y].Length) surf[x + y * w] = surface[y][x];
                }
        }

        public void OnBeforeSerialize()
        {
            Ensure();
            shape = new string[h];
            surface = new string[h];
            var a = new char[w];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++) a[x] = cells[x + y * w] ? '#' : '.';
                shape[y] = new string(a);
                for (int x = 0; x < w; x++) a[x] = surf[x + y * w];
                surface[y] = new string(a);
            }
        }

        public void OnAfterDeserialize()
        {
            cells = null;
            Ensure();
            objects ??= new List<ArenaObject>();
            if (string.IsNullOrEmpty(roof)) roof = RoofAuto;
        }

        public bool Inside(int x, int y) => x >= 0 && y >= 0 && x < w && y < h;

        /// <summary>Клетка входит в форму арены (слой 0).</summary>
        public bool In(int x, int y) { Ensure(); return Inside(x, y) && cells[x + y * w]; }

        public void SetIn(int x, int y, bool v) { Ensure(); if (Inside(x, y)) cells[x + y * w] = v; }

        /// <summary>Поверхность клетки (слой 1), ' ' — нет. Не зависит от формы.</summary>
        public char Surf(int x, int y) { Ensure(); return Inside(x, y) ? surf[x + y * w] : NoSurface; }

        public void SetSurf(int x, int y, char c) { Ensure(); if (Inside(x, y)) surf[x + y * w] = c; }

        /// <summary>Клетка — пол арены: в форме и с поверхностью «пол» (не лава).</summary>
        public bool IsFloor(int x, int y) => In(x, y) && ArenaSurface.Get(Surf(x, y)) is { Lava: false };

        /// <summary>Клетка пойдёт в игру как пол или лава: в форме и с поверхностью.</summary>
        public bool IsGround(int x, int y) => In(x, y) && ArenaSurface.Get(Surf(x, y)) != null;

        public int ShapeCount()
        {
            Ensure();
            int n = 0;
            foreach (bool c in cells) if (c) n++;
            return n;
        }

        /// <summary>Клетки, которые занимает объект (с учётом поворота).</summary>
        public static RectInt Footprint(ArenaObject o)
        {
            var p = ArenaPieces.Get(o.id);
            int pw = p?.W ?? 1, ph = p?.H ?? 1;
            return (o.rot & 1) == 0 ? new RectInt(o.x, o.y, pw, ph) : new RectInt(o.x, o.y, ph, pw);
        }

        /// <summary>Объект слоя layer (или любого из 2–4 при layer &lt; 0), занимающий клетку.</summary>
        public ArenaObject ObjectAt(int x, int y, int layer = -1)
        {
            for (int i = objects.Count - 1; i >= 0; i--)
            {
                var o = objects[i];
                if (layer >= 0 && ArenaPieces.Get(o.id)?.Layer != layer) continue;
                if (Footprint(o).Contains(new Vector2Int(x, y))) return o;
            }
            return null;
        }

        public ArenaLayout Clone()
        {
            OnBeforeSerialize();
            var c = JsonUtility.FromJson<ArenaLayout>(JsonUtility.ToJson(this));
            return c;
        }

        /// <summary>Отражение по горизонтали экрана (как Hades отражает комнаты): камера повёрнута на 45°, поэтому на сетке
        /// это транспонирование — клетки остаются клетками. Направление a → 90° − a.</summary>
        public ArenaLayout Transposed()
        {
            Ensure();
            var t = new ArenaLayout(name, h, w) { roof = roof };
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    t.SetIn(y, x, In(x, y));
                    t.SetSurf(y, x, Surf(x, y));
                }
            foreach (var o in objects) t.objects.Add(new ArenaObject { id = o.id, x = o.y, y = o.x, rot = (5 - o.rot) & 3 });
            return t;
        }

        public string ToJson() { OnBeforeSerialize(); return JsonUtility.ToJson(this, true); }

        public static ArenaLayout FromJson(string json) => JsonUtility.FromJson<ArenaLayout>(json);
    }

    /// <summary>Объект слоя 2–4: что (ArenaPieces), левая нижняя клетка занимаемой области и поворот (×90°, против часовой).</summary>
    [Serializable]
    public sealed class ArenaObject
    {
        public string id;
        public int x, y;
        public int rot;
        /// <summary>Направление (для направленных ловушек), градусы по x, z мира.</summary>
        public float Angle => (rot & 3) * 90f;
    }

    /// <summary>Тип поверхности слоя 1. Новый тип — новая строка в All (код, имя, цвет в редакторе, правило в игре).</summary>
    public sealed class ArenaSurface
    {
        public char Code;
        public string Name;
        public Color Color;
        /// <summary>Лава Hades: пол типа Lava + опасная зона (урон, прыжок рывком через неё — ArenaSim.OnLava).</summary>
        public bool Lava;

        /// <summary>Урон лавы — как у лавы Асфодели (B_Combat01, hazards.damage).</summary>
        public const float LavaDamage = 1.316f;

        public static readonly ArenaSurface Floor = new() { Code = 'f', Name = "Пол", Color = new Color(0.62f, 0.62f, 0.6f) };
        public static readonly ArenaSurface LavaPool = new() { Code = 'l', Name = "Лава", Color = new Color(0.95f, 0.38f, 0.12f), Lava = true };
        public static readonly ArenaSurface[] All = { Floor, LavaPool };

        public static ArenaSurface Get(char code)
        {
            foreach (var s in All) if (s.Code == code) return s;
            return null;
        }
    }

    /// <summary>Объект слоёв 2–4: существующие ловушки Hades (параметры — из комнат Hades, ArenaPieces.TrapSample) и
    /// объекты полиса (вид — PolisKit по типу и занимаемой области).</summary>
    public sealed class ArenaPiece
    {
        public string Id, Name;
        public int Layer, W = 1, H = 1;
        /// <summary>Ловушка: тип Hades (RoomDef.Trap.type).</summary>
        public string Trap;
        /// <summary>Препятствие: тип полиса (PolisDressing.Bench …), высота и держит ли снаряды.</summary>
        public string Polis;
        public float Height;
        public bool Shots;
        /// <summary>Бьёт в одну сторону — поворот задаёт направление.</summary>
        public bool Directional;
    }

    public static class ArenaPieces
    {
        static ArenaPiece T(string id, string name, string type, bool dir = false) => new() { Id = id, Name = name, Layer = ArenaLayout.Traps, Trap = type, Directional = dir };
        static ArenaPiece O(string id, string name, int layer, int w, int h, string polis, float height) =>
            new() { Id = id, Name = name, Layer = layer, W = w, H = h, Polis = polis, Height = height, Shots = layer == ArenaLayout.High };

        /// <summary>Высоты — как у PolisDressing.Classify.</summary>
        public static readonly ArenaPiece[] All =
        {
            T("spike", "Шипы", "SpikeTrap"),
            T("blastcube", "Взрывной куб", "BlastCubeFused"),
            T("blastcube_regen", "Взрывной куб (восстанавливается)", "BlastCubeFusedRegenerating"),
            T("gas", "Газ", "GasTrapPassive"),
            T("poison", "Яд", "PoisonTrap"),
            T("phalanx", "Фаланга", "PhalanxTrap", true),
            T("archer", "Лучник", "ArcherTrap", true),
            T("saw", "Пила", "SawTrap", true),
            O("bench", "Лавка 2×1", ArenaLayout.Low, 2, 1, PolisDressing.Bench, 0.55f),
            O("planter", "Клумба 1×1", ArenaLayout.Low, 1, 1, PolisDressing.Planter, 0.55f),
            O("planter2", "Клумба 2×2", ArenaLayout.Low, 2, 2, PolisDressing.Planter, 0.55f),
            O("column", "Колонна", ArenaLayout.High, 1, 1, PolisDressing.Column, 3.4f),
            O("statue", "Статуя", ArenaLayout.High, 1, 1, PolisDressing.Statue, 2.6f),
            O("lamp", "Фонарь", ArenaLayout.High, 1, 1, PolisDressing.Lamp, 2.8f),
            O("pedestal", "Постамент со статуей 2×2", ArenaLayout.High, 2, 2, PolisDressing.Statue, 2.6f),
            O("wall", "Стенка 2×1", ArenaLayout.High, 2, 1, PolisDressing.Stoa, 2.2f),
            O("pavilion", "Павильон 3×3", ArenaLayout.High, 3, 3, PolisDressing.Pavilion, 2.8f),
        };

        public static ArenaPiece Get(string id)
        {
            foreach (var p in All) if (p.Id == id) return p;
            return null;
        }

        public static List<ArenaPiece> OfLayer(int layer)
        {
            var list = new List<ArenaPiece>();
            foreach (var p in All) if (p.Layer == layer) list.Add(p);
            return list;
        }

        static Dictionary<string, RoomDef.Trap> samples;

        /// <summary>Ловушка этого типа из комнат Hades (первая встреченная) — её параметры и твёрдость.</summary>
        public static RoomDef.Trap TrapSample(string type)
        {
            if (samples == null)
            {
                samples = new Dictionary<string, RoomDef.Trap>();
                foreach (var name in RoomLibrary.All)
                {
                    var d = RoomLibrary.Get(name);
                    if (d == null) continue;
                    foreach (var t in d.traps) if (!samples.ContainsKey(t.type)) samples[t.type] = t;
                }
            }
            return samples.TryGetValue(type, out var s) ? s : null;
        }
    }

    /// <summary>Готовые формы: группы размеров по площади пола арен Тартара (154–482 ю²). Составные — в средних и больших,
    /// рукава не уже 7 клеток. Восьмёрка — два квадрата встык со срезанными углами у стыка.</summary>
    public static class ArenaPresets
    {
        public sealed class Preset
        {
            public string Group, Name;
            public RectInt[] Rects;
            /// <summary>Вырезы (восьмёрка: углы у стыка).</summary>
            public RectInt[] Cuts = Array.Empty<RectInt>();
            public string Title => $"{Group} / {Name}";
        }

        public const string Small = "Маленькие", Medium = "Средние", Large = "Большие";

        static Preset P(string g, string n, params RectInt[] r) => new() { Group = g, Name = n, Rects = r };
        static RectInt R(int x, int y, int w, int h) => new(x, y, w, h);

        static Preset Eight(string g, int side, int notchW, int notchD) => new()
        {
            Group = g, Name = "Восьмёрка", Rects = new[] { R(0, 0, side * 2, side) },
            Cuts = new[] { R(side - notchW / 2, 0, notchW, notchD), R(side - notchW / 2, side - notchD, notchW, notchD) },
        };

        public static readonly Preset[] All =
        {
            P(Small, "Квадрат 13×13", R(0, 0, 13, 13)),
            P(Small, "Прямоугольник 16×11", R(0, 0, 16, 11)),
            P(Medium, "Квадрат 17×17", R(0, 0, 17, 17)),
            P(Medium, "Прямоугольник 21×13", R(0, 0, 21, 13)),
            Eight(Medium, 12, 4, 3),
            P(Medium, "L 19×19", R(0, 0, 19, 9), R(0, 9, 9, 10)),
            P(Medium, "Зигзаг 21×18", R(0, 0, 14, 9), R(7, 9, 14, 9)),
            P(Large, "Квадрат 21×21", R(0, 0, 21, 21)),
            P(Large, "Прямоугольник 26×16", R(0, 0, 26, 16)),
            Eight(Large, 15, 6, 4),
            P(Large, "L 24×24", R(0, 0, 24, 11), R(0, 11, 11, 13)),
            P(Large, "Зигзаг 25×24", R(0, 0, 17, 12), R(8, 12, 17, 12)),
        };

        static bool Has(Preset p, int x, int y)
        {
            var c = new Vector2Int(x, y);
            foreach (var r in p.Cuts) if (r.Contains(c)) return false;
            foreach (var r in p.Rects) if (r.Contains(c)) return true;
            return false;
        }

        public static RectInt Bounds(Preset p)
        {
            int x1 = 0, y1 = 0;
            foreach (var r in p.Rects) { x1 = Mathf.Max(x1, r.xMax); y1 = Mathf.Max(y1, r.yMax); }
            return new RectInt(0, 0, x1, y1);
        }

        /// <summary>Форма пресета по центру сетки арены (только слой 0; поверхность новых клеток — пол, как при рисовании).</summary>
        public static void Apply(ArenaLayout a, Preset p)
        {
            var b = Bounds(p);
            int ox = (a.w - b.width) / 2, oy = (a.h - b.height) / 2;
            for (int y = 0; y < a.h; y++)
                for (int x = 0; x < a.w; x++)
                {
                    bool on = Has(p, x - ox, y - oy);
                    if (on && !a.In(x, y) && a.Surf(x, y) == ArenaLayout.NoSurface) a.SetSurf(x, y, ArenaSurface.Floor.Code);
                    a.SetIn(x, y, on);
                }
        }

        public static int Area(Preset p)
        {
            var b = Bounds(p);
            int n = 0;
            for (int y = 0; y < b.height; y++) for (int x = 0; x < b.width; x++) if (Has(p, x, y)) n++;
            return n;
        }
    }
}
