using System.Linq;
using Arena.Core;
using Arena.Sim;
using NUnit.Framework;
using UnityEngine;

namespace Arena.Tests
{
    /// <summary>Полис и ловушки: ловушки работают только в бою; препятствия арен — городские объекты, мелкие слиты;
    /// хаб застроен кварталами.</summary>
    public class PolisTests
    {
        /// <summary>Этаж: ловушки взведены только в запертой (боевой) комнате; зачищена — снова сброшены.</summary>
        [Test]
        public void FloorTrapsArmOnlyDuringFight()
        {
            var s = new ArenaSim(4);
            s.EnableForms();
            s.StartFloors(4, arenas: true);
            Assert.Greater(s.Traps.Count, 0, "на этаже есть ловушки");
            Assert.IsFalse(s.Traps.Any(s.TrapArmed), "до боя ловушки сброшены");
            var room = s.Floor.Rooms.First(r => r.Arena != null && r.Combat &&
                s.Traps.Any(t => s.Floor.RoomAt(Mathf.FloorToInt(t.Pos.x), Mathf.FloorToInt(t.Pos.y)) == r.Index));
            var trap = s.Traps.First(t => s.Floor.RoomAt(Mathf.FloorToInt(t.Pos.x), Mathf.FloorToInt(t.Pos.y)) == room.Index);
            var at = room.ArenaSpawns.First(p => s.InsideRoom(p.pos, 0.4f) && s.Floor.RoomAt(Mathf.FloorToInt(p.pos.x), Mathf.FloorToInt(p.pos.y)) == room.Index).pos;
            s.Player.Pos = at;
            s.Player.BuffInvuln = 999f;
            for (int i = 0; i < 30 && s.ActiveRoom != room.Index; i++) s.Step(1f / 60f, default);
            Assert.AreEqual(room.Index, s.ActiveRoom);
            Assert.IsTrue(s.TrapArmed(trap), "в бою — взведена");
            Assert.IsFalse(s.Traps.Where(t => s.Floor.RoomAt(Mathf.FloorToInt(t.Pos.x), Mathf.FloorToInt(t.Pos.y)) is int rr && rr >= 0 && rr != room.Index).Any(s.TrapArmed),
                "в других комнатах — нет");
            for (int i = 0; i < 60 * 120 && s.ActiveRoom == room.Index; i++)
            {
                s.Step(1f / 60f, default);
                foreach (var e in s.Enemies) if (e.Alive) s.Damage(e, 99999f, false);
                if (s.RewardPending) s.LeaveLoot();
            }
            Assert.AreEqual(-1, s.ActiveRoom, "комната зачищена");
            Assert.IsFalse(s.TrapArmed(trap), "зачищена — сброшена");
            Assert.IsTrue(s.Pedestal);
            foreach (var g in s.Loot) Assert.IsFalse(s.Occluded(g.Pos, 1f), $"награду {g.Pos} ничто не закрывает от камеры");
            s.Step(1f / 60f, default);
            Assert.AreEqual(TrapPhase.Idle, trap.Phase, "сброшенная стоит");
        }

        /// <summary>Отдельная комната Hades: ловушки взведены, пока комната не зачищена.</summary>
        [Test]
        public void RoomTrapsDisarmWhenCleared()
        {
            var s = new ArenaSim(1, RoomLibrary.Get("A_Combat01"));
            s.EnableForms();
            s.Player.BuffInvuln = 999f;
            Assert.Greater(s.Traps.Count, 0);
            Assert.IsTrue(s.TrapArmed(s.Traps[0]), "бой идёт — взведена");
            for (int i = 0; i < 60 * 120 && !s.DoorsOpen; i++)
            {
                s.Step(1f / 60f, default);
                foreach (var e in s.Enemies) if (e.Alive) s.Damage(e, 99999f, false);
                if (s.RewardPending) s.LeaveLoot();
            }
            Assert.IsTrue(s.DoorsOpen);
            Assert.IsFalse(s.TrapArmed(s.Traps[0]), "зачищена — сброшена");
        }

        /// <summary>Ловушки этажа не закрыты от камеры ни объектами арен, ни стенами залов, ни домами.</summary>
        [Test]
        public void FloorTrapsAreNotHidden()
        {
            foreach (int seed in new[] { 1, 2, 3 })
            {
                var s = new ArenaSim(seed);
                s.EnableForms();
                s.StartFloors(seed, arenas: true);
                foreach (var t in s.Room.traps)
                    Assert.IsFalse(s.Occluded(t.pos, 0.1f), $"#{seed}: ловушка {t.type} в {t.pos} закрыта от камеры");
            }
        }

        /// <summary>Препятствия арен на этаже — городские объекты; мелочи (радиус до 0.5) почти не осталось.</summary>
        [Test]
        public void ArenaObstaclesBecomeCityProps()
        {
            var s = new ArenaSim(2);
            s.EnableForms();
            s.StartFloors(2, arenas: true);
            var visible = s.Room.walls.Where(w => w.h > 0f && w.p.Length >= 3 && w.type != "EtgWall").ToList();
            Assert.Greater(visible.Count, 20);
            var notCity = visible.Where(w => w.type == null || !w.type.StartsWith("Polis/")).Select(w => w.type).Distinct().ToList();
            Assert.IsEmpty(notCity, "все видимые препятствия арен — городские: " + string.Join(", ", notCity));
            foreach (var w in visible.Where(w => w.type is PolisDressing.Bench or PolisDressing.Planter))
                Assert.IsFalse(w.shots, "через лавки и клумбы стреляют");
            foreach (var w in visible.Where(w => w.type is PolisDressing.Pavilion or PolisDressing.Stoa))
                Assert.IsTrue(w.shots, "сквозь павильоны и портики не стреляют");
        }

        [Test]
        public void HubIsAPolisQuarter()
        {
            var s = new ArenaSim(5);
            s.EnableForms();
            s.EnterHub();
            Assert.AreEqual(PolisDressing.Biome, s.Room.biome);
            Assert.Greater(s.Room.city.Length, 10, "вокруг хаба — дома");
            foreach (var c in s.Room.city) Assert.IsFalse(s.InsideRoom(Geo.Centroid(c.p), 0f), $"{c.type} на площади");
            Assert.IsFalse(s.Traps.Any(s.TrapArmed), "в хабе ловушек нет / не работают");
        }
    }
}
