using System.Collections.Generic;
using Arena.Core;
using UnityEngine;

namespace Arena.Sim
{
    /// <summary>Где встаёт пятачок наград: ближайшая к центру комнаты точка, где он помещается целиком и его не закрывает
    /// от камеры ни один объект (если такой нет нигде — просто ближайшая, где помещается).</summary>
    public sealed partial class ArenaSim
    {
        const float PedestalStep = 0.2f;

        /// <summary>Места пятачка уже найденных комнат (геометрия комнаты не меняется, отражённая — отдельно).</summary>
        static readonly Dictionary<string, Vector2> pedestalCache = new();

        /// <summary>half — от середины ряда наград до крайней.
        /// Сетка с шагом 0.2 ю в прямоугольнике lo–hi. Дойти можно по полу без стен и урн (по лаве тоже) — от того места,
        /// где стоит Смерть. Свободно — там, куда можно дойти, кроме лавы и ловушек. Расстояние до занятого — двухпроходная фаска
        /// (шаги 1 и √2). Пятачок помещается, если на его оси (±half по горизонтали экрана) до занятого не меньше
        /// pedestalClear. Если не влезает нигде — отступ меньше, до pedestalClearMin.
        /// center == null — центр масс пола, куда можно дойти; тогда место запоминается для комнаты.</summary>
        Vector2 PedestalSpot(Vector2 lo, Vector2 hi, Vector2? center, float half)
        {
            string key = center == null && !FloorMode ? $"{Room.name}|{Room.Flipped}|{half}" : null;
            if (key != null && pedestalCache.TryGetValue(key, out var cached)) return cached;

            const float step = PedestalStep;
            lo -= Vector2.one; hi += Vector2.one;
            int nx = Mathf.CeilToInt((hi.x - lo.x) / step) + 1, ny = Mathf.CeilToInt((hi.y - lo.y) / step) + 1;
            var walk = new bool[nx * ny];
            for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                var p = new Vector2(lo.x + i * step, lo.y + j * step);
                walk[j * nx + i] = OnFloor(p) && !HitsObstacle(p, 0f);
            }

            // куда можно дойти от Смерти (4 соседа)
            var reach = new bool[nx * ny];
            int si = Mathf.Clamp(Mathf.RoundToInt((Player.Pos.x - lo.x) / step), 0, nx - 1);
            int sj = Mathf.Clamp(Mathf.RoundToInt((Player.Pos.y - lo.y) / step), 0, ny - 1);
            var queue = new Queue<int>();
            queue.Enqueue(sj * nx + si);
            reach[sj * nx + si] = true;
            while (queue.Count > 0)
            {
                int c = queue.Dequeue(), ci = c % nx, cj = c / nx;
                void Visit(int i, int j)
                {
                    if (i < 0 || j < 0 || i >= nx || j >= ny) return;
                    int k = j * nx + i;
                    if (reach[k] || !walk[k]) return;
                    reach[k] = true;
                    queue.Enqueue(k);
                }
                Visit(ci - 1, cj); Visit(ci + 1, cj); Visit(ci, cj - 1); Visit(ci, cj + 1);
            }

            // свободно: дойти можно, не лава и не ловушка; до занятого — фаска в шагах сетки
            var dist = new float[nx * ny];
            Vector2 sum = Vector2.zero;
            int count = 0;
            for (int j = 0; j < ny; j++)
            for (int i = 0; i < nx; i++)
            {
                int k = j * nx + i;
                if (!reach[k]) continue;
                var p = new Vector2(lo.x + i * step, lo.y + j * step);
                sum += p; count++;
                bool edge = i == 0 || j == 0 || i == nx - 1 || j == ny - 1;
                dist[k] = edge || InHazard(p) || OnTrap(p) ? 0f : float.MaxValue;
            }
            const float diag = 1.41421356f;
            for (int j = 1; j < ny; j++)
            for (int i = 1; i < nx - 1; i++)
            {
                int k = j * nx + i;
                if (dist[k] == 0f) continue;
                dist[k] = Mathf.Min(dist[k], dist[k - 1] + 1f, dist[k - nx] + 1f, dist[k - nx - 1] + diag, dist[k - nx + 1] + diag);
            }
            for (int j = ny - 2; j >= 0; j--)
            for (int i = nx - 2; i >= 1; i--)
            {
                int k = j * nx + i;
                if (dist[k] == 0f) continue;
                dist[k] = Mathf.Min(dist[k], dist[k + 1] + 1f, dist[k + nx] + 1f, dist[k + nx + 1] + diag, dist[k + nx - 1] + diag);
            }

            var mid = center ?? (count > 0 ? sum / count : Player.Pos);
            var cand = new List<int>();
            for (int k = 0; k < dist.Length; k++) if (dist[k] > 0f) cand.Add(k);
            Vector2 At(int k) => new(lo.x + (k % nx) * step, lo.y + (k / nx) * step);
            cand.Sort((a, b) => (At(a) - mid).sqrMagnitude.CompareTo((At(b) - mid).sqrMagnitude));

            float Clearance(Vector2 p)
            {
                int i = Mathf.RoundToInt((p.x - lo.x) / step), j = Mathf.RoundToInt((p.y - lo.y) / step);
                return i < 0 || j < 0 || i >= nx || j >= ny ? 0f : dist[j * nx + i] * step;
            }

            var spot = Player.Pos;
            var cfg = ArenaCfg;
            float[] needs = { cfg.pedestalClear, (cfg.pedestalClear + cfg.pedestalClearMin) * 0.5f, cfg.pedestalClearMin };
            // сначала — места, где награду не закрывает от камеры ни один объект; не нашлось нигде — как получится
            bool done = false;
            for (int pass = 0; pass < 2 && !done; pass++)
                foreach (float need in needs)
                {
                    int found = -1;
                    foreach (int k in cand)
                    {
                        var c = At(k);
                        bool fits = true;
                        for (int s = 0; s <= 6 && fits; s++)
                            fits = Clearance(c + ScreenRight * (half * (s / 3f - 1f))) >= need;
                        if (fits && pass == 0)
                            for (int s = -1; s <= 1 && fits; s++)
                                fits = !Occluded(c + ScreenRight * (half * s), 1.0f);
                        if (fits) { found = k; break; }
                    }
                    if (found >= 0) { spot = At(found); done = true; break; }
                }
            if (key != null) pedestalCache[key] = spot;
            return spot;
        }

        // ---------- что закрывает от камеры ----------

        readonly List<(Vector2 c, float r, float h)> hides = new();
        RoomDef hidesRoom;
        int hidesCount = -1;

        /// <summary>Стена ближнего края: за ней (дальше от камеры) пол комнаты, а перед ней (к камере) пола нет. В комнате
        /// Hades её рисуют низкой (RoomView.FrontWall), чтобы не закрывала бой; колонны посреди комнаты под это не попадают.</summary>
        public bool IsFrontWall(Obstacle o)
        {
            var camFwd = new Vector2(0.70710678f, 0.70710678f);
            bool behind = false;
            for (float d = 0.6f; d <= 3f && !behind; d += 0.6f)
                if (InsideRoom(o.Pos + camFwd * (o.R + d), 0f)) behind = true;
            if (!behind) return false;
            for (float d = 0.6f; d <= 3f; d += 0.6f)
                if (InsideRoom(o.Pos - camFwd * (o.R + d), 0f)) return false;
            return true;
        }

        /// <summary>Точку p на высоте y закрывает от камеры что-то перед ней (PolisDressing.Hides): объект комнаты — той высоты,
        /// какой его рисуют (в комнате Hades — до 1.8, стена ближнего края — 0.4), дом полиса, стена зала. Решётки — не в счёт.</summary>
        public bool Occluded(Vector2 p, float y)
        {
            if (hidesRoom != Room || hidesCount != Obstacles.Count) BuildHides();
            foreach (var (c, r, h) in hides) if (PolisDressing.Hides(c, r, h, p, y)) return true;
            return false;
        }

        void BuildHides()
        {
            hidesRoom = Room;
            hidesCount = Obstacles.Count;
            hides.Clear();
            bool polis = Room.biome == PolisDressing.Biome;
            foreach (var o in Obstacles)
            {
                if (o.Type == "Bars" || !float.IsFinite(o.Pos.x) || !float.IsFinite(o.R)) continue;
                float h = polis ? o.Height : Mathf.Min(o.Height, 1.8f);
                if (!polis && h > 0.4f && IsFrontWall(o)) h = 0.4f; // как рисует RoomView
                if (h > 0.3f) hides.Add((o.Pos, o.R, h));
            }
            foreach (var b in Room.city)
            {
                if (b.p.Length < 3) continue;
                var c = Geo.Centroid(b.p);
                float r = 0f;
                foreach (var v in b.p) r = Mathf.Max(r, (v - c).magnitude);
                hides.Add((c, r, b.h + 0.8f)); // с крышей
            }
            if (FloorMode && Floor.Polygonal)
                foreach (var room in Floor.Rooms)
                {
                    if (!room.Indoor || room.Arena == null) continue;
                    var doors = ArenaFloorBuilder.DoorPoints(room);
                    var off = room.ArenaOffset;
                    foreach (var outline in room.Arena.Outlines)
                    {
                        var poly = new Vector2[outline.Length];
                        for (int i = 0; i < poly.Length; i++) poly[i] = outline[i] + off;
                        foreach (var (c, r) in PolisDressing.HallPieces(poly, doors)) hides.Add((c, r, PolisDressing.HallHeight));
                    }
                }
        }

        bool InHazard(Vector2 p)
        {
            foreach (var h in Hazards)
                if (Geo.InPoly(h.Poly, p)) return true;
            return false;
        }

        bool OnTrap(Vector2 p)
        {
            foreach (var t in Traps)
            {
                if ((p - t.Pos).sqrMagnitude < 0.25f) return true;
                if (t.Poly != null && t.Poly.Length >= 3 && Geo.InPoly(t.Poly, p)) return true;
            }
            return false;
        }
    }
}
