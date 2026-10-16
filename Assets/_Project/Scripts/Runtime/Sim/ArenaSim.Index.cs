using System.Collections.Generic;
using Arena.Core;
using UnityEngine;

namespace Arena.Sim
{
    /// <summary>Индекс препятствий и контуров пола по клеткам 4×4 ю — для этажа из арен, где их сотни. Пока препятствий
    /// и контуров немного (отдельная комната Hades), проверки перебирают все, как раньше. Индекс препятствий
    /// пересобирается, когда меняется их число (разбита урна, поднялась или опустилась решётка).</summary>
    public sealed partial class ArenaSim
    {
        const float IndexCell = 4f;
        const int ObstacleIndexMin = 160, FloorIndexMin = 24;

        List<Obstacle>[] obsCells;
        int obsCount = -1, obsW, obsH, obsStamp;
        Vector2 obsMin;
        readonly List<Obstacle> obsNear = new();
        readonly List<Obstacle> obsAlways = new(); // центр или радиус не число (вырожденный контур) — проверяются всегда

        RoomDef.Poly[] floorIndexed;
        List<RoomDef.Poly>[] floorCells;
        int floorW, floorH;
        Vector2 floorMin;

        /// <summary>Препятствия, которые могут задеть прямоугольник lo–hi (точная проверка — у вызывающего).</summary>
        List<Obstacle> ObstaclesNear(Vector2 lo, Vector2 hi)
        {
            if (Obstacles.Count < ObstacleIndexMin) return Obstacles;
            if (obsCells == null || obsCount != Obstacles.Count) BuildObstacleIndex();
            obsNear.Clear();
            obsNear.AddRange(obsAlways);
            obsStamp++;
            int x0 = Mathf.Clamp(Mathf.FloorToInt((lo.x - obsMin.x) / IndexCell), 0, obsW - 1), x1 = Mathf.Clamp(Mathf.FloorToInt((hi.x - obsMin.x) / IndexCell), 0, obsW - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt((lo.y - obsMin.y) / IndexCell), 0, obsH - 1), y1 = Mathf.Clamp(Mathf.FloorToInt((hi.y - obsMin.y) / IndexCell), 0, obsH - 1);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    var cell = obsCells[x + y * obsW];
                    if (cell == null) continue;
                    foreach (var o in cell)
                        if (o.Stamp != obsStamp) { o.Stamp = obsStamp; obsNear.Add(o); }
                }
            return obsNear;
        }

        List<Obstacle> ObstaclesNear(Vector2 p, float r) => ObstaclesNear(p - new Vector2(r, r), p + new Vector2(r, r));

        void BuildObstacleIndex()
        {
            obsCount = Obstacles.Count;
            obsAlways.Clear();
            var lo = new Vector2(float.MaxValue, float.MaxValue);
            var hi = new Vector2(float.MinValue, float.MinValue);
            static bool Finite(Obstacle o) => float.IsFinite(o.Pos.x) && float.IsFinite(o.Pos.y) && float.IsFinite(o.R) && o.R >= 0f;
            foreach (var o in Obstacles)
            {
                if (!Finite(o)) continue;
                lo = Vector2.Min(lo, o.Pos - new Vector2(o.R, o.R)); hi = Vector2.Max(hi, o.Pos + new Vector2(o.R, o.R));
            }
            if (lo.x > hi.x) { lo = hi = Vector2.zero; }
            obsMin = lo;
            obsW = Mathf.Max(1, Mathf.CeilToInt((hi.x - lo.x) / IndexCell) + 1);
            obsH = Mathf.Max(1, Mathf.CeilToInt((hi.y - lo.y) / IndexCell) + 1);
            obsCells = new List<Obstacle>[obsW * obsH];
            foreach (var o in Obstacles)
            {
                if (!Finite(o)) { obsAlways.Add(o); continue; }
                int x0 = Mathf.Clamp(Mathf.FloorToInt((o.Pos.x - o.R - lo.x) / IndexCell), 0, obsW - 1), x1 = Mathf.Clamp(Mathf.FloorToInt((o.Pos.x + o.R - lo.x) / IndexCell), 0, obsW - 1);
                int y0 = Mathf.Clamp(Mathf.FloorToInt((o.Pos.y - o.R - lo.y) / IndexCell), 0, obsH - 1), y1 = Mathf.Clamp(Mathf.FloorToInt((o.Pos.y + o.R - lo.y) / IndexCell), 0, obsH - 1);
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                        (obsCells[x + y * obsW] ??= new List<Obstacle>()).Add(o);
            }
        }

        /// <summary>Пересобрать индекс препятствий при следующей проверке (комната перестроена целиком).</summary>
        void InvalidateObstacleIndex() => obsCells = null;

        /// <summary>Контуры пола, которые могут содержать точку p.</summary>
        IReadOnlyList<RoomDef.Poly> FloorsAt(Vector2 p)
        {
            var floor = Room.floor;
            if (floor.Length < FloorIndexMin) return floor;
            if (floorIndexed != floor) BuildFloorIndex(floor);
            int x = Mathf.FloorToInt((p.x - floorMin.x) / IndexCell), y = Mathf.FloorToInt((p.y - floorMin.y) / IndexCell);
            if (x < 0 || y < 0 || x >= floorW || y >= floorH) return System.Array.Empty<RoomDef.Poly>();
            return (IReadOnlyList<RoomDef.Poly>)floorCells[x + y * floorW] ?? System.Array.Empty<RoomDef.Poly>();
        }

        void BuildFloorIndex(RoomDef.Poly[] floor)
        {
            floorIndexed = floor;
            var lo = new Vector2(float.MaxValue, float.MaxValue);
            var hi = new Vector2(float.MinValue, float.MinValue);
            foreach (var f in floor) foreach (var v in f.p) if (float.IsFinite(v.x) && float.IsFinite(v.y)) { lo = Vector2.Min(lo, v); hi = Vector2.Max(hi, v); }
            if (lo.x > hi.x) { lo = hi = Vector2.zero; }
            floorMin = lo;
            floorW = Mathf.Max(1, Mathf.CeilToInt((hi.x - lo.x) / IndexCell) + 1);
            floorH = Mathf.Max(1, Mathf.CeilToInt((hi.y - lo.y) / IndexCell) + 1);
            floorCells = new List<RoomDef.Poly>[floorW * floorH];
            foreach (var f in floor)
            {
                var a = new Vector2(float.MaxValue, float.MaxValue);
                var b = new Vector2(float.MinValue, float.MinValue);
                foreach (var v in f.p) if (float.IsFinite(v.x) && float.IsFinite(v.y)) { a = Vector2.Min(a, v); b = Vector2.Max(b, v); }
                if (a.x > b.x) continue;
                int x0 = Mathf.Clamp(Mathf.FloorToInt((a.x - lo.x) / IndexCell), 0, floorW - 1), x1 = Mathf.Clamp(Mathf.FloorToInt((b.x - lo.x) / IndexCell), 0, floorW - 1);
                int y0 = Mathf.Clamp(Mathf.FloorToInt((a.y - lo.y) / IndexCell), 0, floorH - 1), y1 = Mathf.Clamp(Mathf.FloorToInt((b.y - lo.y) / IndexCell), 0, floorH - 1);
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                        (floorCells[x + y * floorW] ??= new List<RoomDef.Poly>()).Add(f);
            }
        }
    }
}
