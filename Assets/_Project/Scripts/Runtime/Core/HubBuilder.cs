using System.Collections.Generic;
using UnityEngine;

namespace Arena.Core
{
    public enum HubSpotKind { Gate, Npc }

    /// <summary>Что делает персонаж хаба: Наставник — наборы карт на забег, Торговец — открытие за Сардис, Хранитель — коллекция,
    /// Летописец — летопись забегов.</summary>
    public enum HubRole { None, Mentor, Merchant, Keeper, Chronicler }

    /// <summary>Место в хабе, с которым можно взаимодействовать: врата в забег или персонаж (пока заглушка с репликами).</summary>
    public sealed class HubSpot
    {
        public HubSpotKind Kind;
        public string Name;
        public Vector2 Pos;
        public Color Color;
        public string[] Lines = System.Array.Empty<string>();
        /// <summary>Что открывает «взаимодействие» с персонажем (None — только реплика).</summary>
        public HubRole Role;
    }

    /// <summary>Хаб, как Breach в Enter the Gungeon: площадь полиса между забегами, вокруг — кварталы. Строится в коде, координаты —
    /// в осях экрана (вправо и вверх по экрану) и поворачиваются под камеру. Внизу — где появляется Смерть, наверху за
    /// мостом — врата в забег, по бокам — места персонажей (пока заглушки), справа — мишени для скиллов.</summary>
    public static class HubBuilder
    {
        public const string Name = "Убежище";

        /// <summary>Горизонталь и вертикаль экрана на полу (камера Yaw 45).</summary>
        static readonly Vector2 Right = new(0.70710678f, -0.70710678f), Up = new(0.70710678f, 0.70710678f);

        static Vector2 S(float x, float y) => Right * x + Up * y;

        public static RoomDef Build(out List<HubSpot> spots, out List<Vector2> dummies)
        {
            var floor = new List<RoomDef.Poly>();
            var walls = new List<RoomDef.Poly>();

            // площадь — восьмиугольник, к вратам — мост, у врат — площадка
            floor.Add(Poly(Ngon(0f, 0f, 13f, 8, 22.5f)));
            floor.Add(Poly(Rect(-2.5f, 11f, 2.5f, 19f)));
            floor.Add(Poly(Ngon(0f, 22f, 4.5f, 8, 22.5f)));

            // колонны по краю площади (кроме прохода к мосту и переднего края — он к камере)
            for (int i = 0; i < 12; i++)
            {
                float a = (i + 0.5f) * Mathf.PI * 2f / 12f;
                var c = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 11.2f;
                if (c.y < -2f || Mathf.Abs(c.x) < 3f && c.y > 0f) continue;
                walls.Add(Wall(Ngon(c.x, c.y, 0.7f, 8, 0f), 3.4f, PolisDressing.Column));
            }
            // задняя стена за площадью — по дальнему краю, с проёмом к мосту
            walls.Add(Wall(Rect(-12f, 12.3f, -3.2f, 13.5f), 2.4f, PolisDressing.Stoa));
            walls.Add(Wall(Rect(3.2f, 12.3f, 12f, 13.5f), 2.4f, PolisDressing.Stoa));
            // врата: два столба по бокам площадки
            walls.Add(Wall(Ngon(-2.6f, 24.2f, 0.6f, 8, 0f), 3.4f, PolisDressing.Column));
            walls.Add(Wall(Ngon(2.6f, 24.2f, 0.6f, 8, 0f), 3.4f, PolisDressing.Column));

            spots = new List<HubSpot>
            {
                new() { Kind = HubSpotKind.Gate, Name = "Врата", Pos = S(0f, 23.2f), Color = new Color(0.7f, 0.45f, 1f) },
                new()
                {
                    Kind = HubSpotKind.Npc, Name = "Торговец", Pos = S(-8f, 4f), Color = new Color(1f, 0.75f, 0.35f), Role = HubRole.Merchant,
                    Lines = new[] { "Сардис — за новые формы, свойства и тринкеты. Открытое начнёт попадаться в забеге." },
                },
                new()
                {
                    Kind = HubSpotKind.Npc, Name = "Хранитель", Pos = S(-8.5f, -3f), Color = new Color(0.45f, 0.85f, 1f), Role = HubRole.Keeper,
                    Lines = new[] { "Всё, что ты открыл, хранится у меня: формы, свойства, тринкеты и найденные связки." },
                },
                new()
                {
                    Kind = HubSpotKind.Npc, Name = "Летописец", Pos = S(-3.5f, 8.5f), Color = new Color(0.85f, 0.85f, 0.75f), Role = HubRole.Chronicler,
                    Lines = new[] { "Каждый забег — новая страница летописи." },
                },
                new()
                {
                    Kind = HubSpotKind.Npc, Name = "Наставник", Pos = S(4.5f, -1f), Color = new Color(0.55f, 0.95f, 0.5f), Role = HubRole.Mentor,
                    Lines = new[] { "Возьми один из трёх наборов — с ним пойдёшь в забег. Мишени справа не умирают — пробуй на них." },
                },
            };
            dummies = new List<Vector2> { S(7.5f, 2.5f), S(9f, -1.5f), S(7f, -4.5f) };
            // персонаж стоит на низком постаменте — сквозь него не пройти
            foreach (var sp in spots)
                if (sp.Kind == HubSpotKind.Npc) walls.Add(Wall(NgonAt(sp.Pos, 0.55f, 8), 0.25f, "HubNpcBase"));

            var all = new List<Vector2>();
            foreach (var f in floor) all.AddRange(f.p);
            var min = all[0]; var max = all[0];
            foreach (var v in all) { min = Vector2.Min(min, v); max = Vector2.Max(max, v); }
            var (blocked, buildable, cw, ch, origin) = PolisDressing.MasksFromFloor(floor, 0.3f, 9f);
            var city = PolisDressing.Build(blocked, buildable, cw, ch, origin, 20251016);
            return new RoomDef
            {
                name = Name, biome = PolisDressing.Biome, source = "Хаб (свой)", zoom = 0.9f, city = city.ToArray(),
                heroStart = S(0f, -8f), heroAngle = Mathf.Atan2(Up.y, Up.x) * Mathf.Rad2Deg,
                min = min, max = max, floor = floor.ToArray(), walls = walls.ToArray(),
                encounter = new RoomDef.Encounter { name = "Hub", generated = false },
            };
        }

        static Vector2[] Ngon(float cx, float cy, float r, int n, float startDeg)
        {
            var p = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                float a = (startDeg + i * 360f / n) * Mathf.Deg2Rad;
                p[i] = S(cx + Mathf.Cos(a) * r, cy + Mathf.Sin(a) * r);
            }
            return p;
        }

        static Vector2[] NgonAt(Vector2 c, float r, int n)
        {
            var p = new Vector2[n];
            for (int i = 0; i < n; i++) { float a = i * Mathf.PI * 2f / n; p[i] = c + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r; }
            return p;
        }

        static Vector2[] Rect(float x0, float y0, float x1, float y1) => new[] { S(x0, y0), S(x1, y0), S(x1, y1), S(x0, y1) };

        static RoomDef.Poly Poly(Vector2[] p) => new() { p = p, type = "HubFloor", h = 0f, shots = true };

        static RoomDef.Poly Wall(Vector2[] p, float h, string type) => new() { p = p, type = type, h = h, shots = true };
    }
}
