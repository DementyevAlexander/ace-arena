using System.Linq;
using Arena.Core;
using Arena.Sim;
using NUnit.Framework;
using UnityEngine;

namespace Arena.Tests
{
    /// <summary>Ход боя по комнатам этажа: волны — заново в каждой комнате, мини-босс — без волн, хабы и коннекторы — с боем;
    /// взрывной куб взрывается и вне боя; урон со временем не трясёт врага.</summary>
    public class RoomFlowTests
    {
        static ArenaSim NewRun()
        {
            var s = new ArenaSim(5);
            s.EnableForms();
            s.EnterHub();
            s.StartRun();
            s.Player.BuffInvuln = 9999f;
            return s;
        }

        /// <summary>Встать в комнату, дождаться её боя, добить всех; вернуть наибольший номер волны в ней.</summary>
        static int FightRoom(ArenaSim s, int r, out int firstWave)
        {
            s.Player.Pos = s.Floor.Rooms[r].Center;
            firstWave = -1;
            int maxWave = 0;
            for (int i = 0; i < 60 * 120 && s.RoomStates[r] != ArenaSim.FloorRoomState.Cleared; i++)
            {
                s.Step(T.Dt, default);
                if (s.ActiveRoom == r && s.Wave > 0 && firstWave < 0) firstWave = s.Wave;
                maxWave = Mathf.Max(maxWave, s.ActiveRoom == r ? s.Wave : 0);
                foreach (var e in s.Enemies) if (e.Alive && e.Kind != EnemyKind.Megaera) s.Damage(e, 1e7f, false);
                if (s.RewardPending) s.LeaveLoot();
            }
            Assert.AreEqual(ArenaSim.FloorRoomState.Cleared, s.RoomStates[r], $"комната {r} пройдена");
            return maxWave;
        }

        [Test]
        public void WavesRestartInEveryRoom()
        {
            var s = NewRun();
            var rooms = Enumerable.Range(0, s.Floor.Rooms.Count).Where(r => s.Floor.Rooms[r].Combat && s.Floor.Rooms[r].Elite == 0).Take(3).ToList();
            Assert.AreEqual(3, rooms.Count);
            foreach (int r in rooms)
            {
                int max = FightRoom(s, r, out int first);
                Assert.AreEqual(1, first, $"комната {r}: отсчёт волн — с первой");
                Assert.GreaterOrEqual(max, 1);
            }
        }

        [Test]
        public void MiniBossRoomHasNoWaves()
        {
            var s = NewRun();
            int r = Enumerable.Range(0, s.Floor.Rooms.Count).First(i => s.Floor.Rooms[i].Elite == 1);
            s.Player.Pos = s.Floor.Rooms[r].Center;
            for (int i = 0; i < 300 && s.ActiveRoom != r; i++) s.Step(T.Dt, default);
            T.Run(s, 6f);
            Assert.AreEqual(1, s.Enemies.Count(e => !e.Dead), "в комнате только мини-босс");
            Assert.IsTrue(s.Enemies.Single(e => !e.Dead).Elite);
            foreach (var e in s.Enemies) s.Damage(e, 1e7f, false);
            for (int i = 0; i < 120 && s.RoomStates[r] != ArenaSim.FloorRoomState.Cleared; i++) s.Step(T.Dt, default);
            Assert.AreEqual(ArenaSim.FloorRoomState.Cleared, s.RoomStates[r], "мини-босс убит — комната пройдена, волн нет");
            Assert.AreEqual(0, s.Wave);
        }

        [Test]
        public void HubsAndConnectorsAreCombatRooms()
        {
            var s = NewRun();
            for (int f = 0; f < ArenaSim.FloorOrder.Length; f++)
            {
                foreach (var room in s.Floor.Rooms)
                    if (room.Kind is EtgRoomKind.Hub or EtgRoomKind.Connector) Assert.IsTrue(room.Combat, $"этаж {f + 1}: {room.Kind} — с боем");
                if (f < ArenaSim.FloorOrder.Length - 1) s.EnterNextFloor();
            }
        }

        /// <summary>Взрывной куб: удар поджигает фитиль и после зачистки комнаты — через фитиль он взрывается.</summary>
        [Test]
        public void BlastCubeExplodesOutsideFight()
        {
            var s = new ArenaSim(7, RoomLibrary.Get("A_Combat09"));
            s.EnableForms();
            s.Player.BuffInvuln = 9999f;
            for (int i = 0; i < 60 * 120 && !s.DoorsOpen; i++)
            {
                s.Step(T.Dt, default);
                foreach (var e in s.Enemies) if (e.Alive) s.Damage(e, 1e7f, false);
            }
            Assert.IsTrue(s.DoorsOpen, "комната пройдена");
            var cube = s.Traps.First(t => t.Kind == TrapKind.BlastCube && t.Phase == TrapPhase.Idle);
            s.HitRoomObjects(cube.Pos, 0.3f);
            Assert.AreEqual(TrapPhase.Pre, cube.Phase, "фитиль загорелся");
            T.Run(s, 3f);
            Assert.AreNotEqual(TrapPhase.Pre, cube.Phase, "взорвался");
            Assert.IsTrue(cube.Phase is TrapPhase.Gone or TrapPhase.Idle);
        }

        [Test]
        public void DamageOverTimeDoesNotShake()
        {
            var s = new ArenaSim(5, RoomDef.Box(30f, 30f)) { WavesEnabled = false };
            s.EnableForms();
            var e = T.Spawn(s, EnemyKind.Thug, new Vector2(0f, 4f));
            s.Damage(e, 5f, false, true, DotKind.Burn);
            Assert.LessOrEqual(e.Shake, 0f, "горение не трясёт");
            s.Damage(e, 5f, false);
            Assert.Greater(e.Shake, 0f, "удар трясёт");
        }
    }
}
