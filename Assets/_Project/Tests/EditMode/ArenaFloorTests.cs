using System.Collections.Generic;
using System.Linq;
using Arena.Core;
using Arena.Sim;
using NUnit.Framework;
using UnityEngine;

namespace Arena.Tests
{
    /// <summary>Этаж из арен Hades: все пять схем собираются; обычные комнаты и босс — арены Тартара; один босс и два
    /// мини-босса; от входа пешком (по мостам, с радиусом Смерти) доходишь до каждой комнаты.</summary>
    public class ArenaFloorTests
    {
        [Test]
        public void EveryFloorBuildsWithArenas()
        {
            foreach (var id in ArenaSim.FloorOrder)
                for (int seed = 0; seed < 3; seed++)
                {
                    var s = new ArenaSim(seed);
                    s.EnableForms();
                    s.StartFloors(seed, arenas: true);
                    var f = s.Floor;
                    if (id != ArenaSim.FloorOrder[0])
                    {
                        // нужный этаж: проходим вперёд, пока не дойдём до него
                        while (f.FloorId != id) { s.EnterNextFloor(); f = s.Floor; }
                    }
                    string tag = $"{id} #{seed} {f.Flow}";
                    Assert.IsTrue(f.Polygonal, tag);
                    Assert.IsTrue(s.ArenaFloorsOn, tag);
                    Assert.AreEqual(PolisDressing.Biome, s.Room.biome, tag);
                    Assert.Greater(s.Room.city.Length, 20, $"{tag}: вокруг — кварталы");
                    Assert.IsTrue(f.Rooms.Where(r => r.Kind == EtgRoomKind.Boss && r.Arena != null).All(r => r.Indoor), $"{tag}: босс — в зале");
                    // дома не стоят на проходимом
                    foreach (var c in s.Room.city)
                        Assert.IsFalse(s.InsideRoom(Geo.Centroid(c.p), 0f), $"{tag}: {c.type} на проходимом {Geo.Centroid(c.p)}");
                    int arenas = f.Rooms.Count(r => r.Arena != null);
                    int combat = f.Rooms.Count(r => r.Combat);
                    Assert.Greater(arenas, combat / 2, $"{tag}: бои — в основном на аренах ({arenas} из {combat})");
                    Assert.AreEqual(1, f.Rooms.Count(r => r.Elite == 2), $"{tag}: один босс");
                    Assert.AreEqual(2, f.Rooms.Count(r => r.Elite == 1), $"{tag}: два мини-босса");
                    var hubs = f.Rooms.Where(r => r.Kind == EtgRoomKind.Hub).ToList();
                    Assert.IsTrue(hubs.All(r => r.Combat), $"{tag}: хабы — с боем");
                    Assert.IsTrue(f.Rooms.Where(r => r.Kind == EtgRoomKind.Connector).All(r => r.Combat), $"{tag}: коннекторы — с боем");
                    Assert.IsTrue(s.InsideRoom(s.Player.Pos, s.PlayerCfg.radius), $"{tag}: старт на полу");
                    AssertWalkable(s, tag);
                }
        }

        /// <summary>Поиск в ширину по сетке 0.25 ю от старта: клетка проходима, если там пол и круг Смерти ни во что не упирается.
        /// Каждая комната достижима — хотя бы одна её клетка пола (у арены — точка появления врагов).</summary>
        internal static void AssertWalkable(ArenaSim s, string tag)
        {
            var f = s.Floor;
            const float step = 0.25f;
            float r = s.PlayerCfg.radius;
            var b = f.Bounds;
            int w = Mathf.CeilToInt(b.width / step), h = Mathf.CeilToInt(b.height / step);
            var seen = new bool[w * h];
            Vector2 At(int i, int j) => new(b.xMin + (i + 0.5f) * step, b.yMin + (j + 0.5f) * step);
            int si = Mathf.FloorToInt((s.Player.Pos.x - b.xMin) / step), sj = Mathf.FloorToInt((s.Player.Pos.y - b.yMin) / step);
            var q = new Queue<int>();
            q.Enqueue(si + sj * w); seen[si + sj * w] = true;
            var reached = new bool[f.Rooms.Count];
            while (q.Count > 0)
            {
                int k = q.Dequeue(), i = k % w, j = k / w;
                var p = At(i, j);
                int room = f.RoomAt(Mathf.FloorToInt(p.x), Mathf.FloorToInt(p.y));
                if (room >= 0) reached[room] = true;
                foreach (var (di, dj) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    int ni = i + di, nj = j + dj;
                    if (ni < 0 || nj < 0 || ni >= w || nj >= h || seen[ni + nj * w]) continue;
                    if (!s.InsideRoom(At(ni, nj), r)) continue;
                    seen[ni + nj * w] = true;
                    q.Enqueue(ni + nj * w);
                }
            }
            var missed = f.Rooms.Where(x => !reached[x.Index]).Select(x => $"{x.Kind} {(x.Arena?.Name ?? x.Room.name)}").ToList();
            if (missed.Count == 0) return;
            // что мешает: проход от дошедшей комнаты к недошедшей — клетки его двери и моста до соседа
            var why = new List<string>();
            foreach (var room in f.Rooms.Where(x => reached[x.Index]))
                foreach (var d in room.Doors.Where(d => !reached[d.To]))
                {
                    var other = f.Rooms[d.To];
                    var a = new Vector2(d.Cells[0].x + 0.5f, d.Cells[0].y + 0.5f);
                    var back = other.Doors.First(x => x.To == room.Index);
                    var bpt = new Vector2(back.Cells[0].x + 0.5f, back.Cells[0].y + 0.5f);
                    // от двери соседа — внутрь него, до его пола
                    var dv = EtgData.DirVec(back.Dir);
                    var inner = bpt - new Vector2(dv.x, dv.y) * 12f;
                    var ends = new List<(Vector2 from, Vector2 to)> { (a, bpt), (bpt, inner) };
                    foreach (var (pa, pb) in ends)
                    for (float t = 0f; t <= 1f; t += 0.02f)
                    {
                        var p = Vector2.Lerp(pa, pb, t);
                        if (s.InsideRoom(p, r)) continue;
                        var hits = s.Obstacles.Where(o => (p - o.Pos).magnitude < o.R + r + 0.01f && (o.Round || Geo.Overlaps(o.Poly, p, r))).Select(o => $"{o.Type} h{o.Height:0.#}").ToList();
                        why.Add($"{(room.Arena?.Name ?? room.Room.name)}→{(other.Arena?.Name ?? other.Room.name)} ({p.x:0.0}, {p.y:0.0}): " +
                                (hits.Count > 0 ? string.Join("; ", hits) : s.InsideRoom(p, 0f) ? "край пола ближе радиуса" : "нет пола"));
                        break;
                    }
                }
            Assert.Fail($"{tag}: не дойти пешком до " + string.Join(", ", missed) + "\n" + string.Join("\n", why));
        }

        /// <summary>Арена Hades как шаблон ETG: у каждой арены Тартара и её отражения есть вход и хотя бы один выход
        /// по сторонам света, клетки выходов — на полу шаблона.</summary>
        [Test]
        public void TartarusArenasBecomeTemplates()
        {
            var defs = RoomLibrary.All.Select(RoomLibrary.Get).Where(d => d != null && d.biome == ArenaSim.ArenaBiome).ToList();
            var t = ArenaTemplates.Build(defs, ArenaSim.ScreenRight);
            Assert.AreEqual(defs.Count * 2, t.Count, "каждая арена и её отражение");
            foreach (var a in t)
            {
                Assert.GreaterOrEqual(a.Grid.exits.Length, 2, a.Name);
                foreach (var e in a.Grid.exits)
                    foreach (var c in e.cells) Assert.AreEqual('.', a.Grid.At(c.x, c.y), $"{a.Name}: клетка выхода на полу");
            }
        }
    }
}
