using Arena.Core;
using Arena.Sim;
using NUnit.Framework;
using UnityEngine;

namespace Arena.Tests
{
    public class ThugTests
    {
        static (ArenaSim, Enemy) Setup(float distance = 1.4f)
        {
            var s = T.NewSim();
            var e = T.Spawn(s, EnemyKind.Thug, new Vector2(0f, distance));
            e.Aggro = true;
            return (s, e);
        }

        [Test]
        public void WindupFlashesWhiteThenTrapezoidHits()
        {
            var (s, e) = Setup();
            s.Step(T.Dt, default);
            Assert.AreEqual(AiState.Windup, e.State, "в радиусе AIAttackDistance сразу замах");
            Assert.AreEqual(0.4f, e.WarnTotal, 1e-4f, "вспышка — последние min(0.5, 0.4) с");
            T.Run(s, 0.3f);
            Assert.Greater(e.WarnT, 0.5f, "к концу замаха враг белеет");
            T.Run(s, 0.4f);
            Assert.AreEqual(500f - 100f, s.Player.Hp, 0.01f, "10 урона Hades × 10");
            Assert.Greater(s.Player.Stun, 0f, "ZagreusOnHitStun 0.2 с");
            Assert.Less(s.Player.Pos.y, -0.1f, "отбрасывание назад от Thug");
        }

        [Test]
        public void DirectionIsLockedSoSidestepDodges()
        {
            var (s, e) = Setup();
            s.Step(T.Dt, default);
            T.Run(s, 0.53f, new SimInput { Move = Vector2.right });
            T.Run(s, 0.3f);
            Assert.AreEqual(500f, s.Player.Hp, 0.01f, "AITrackTargetDuringCharge = false: удар туда, куда смотрел в начале");
        }

        [Test]
        public void StunDuringWindupRestartsIt()
        {
            var (s, e) = Setup();
            s.Step(T.Dt, default);
            T.Run(s, 0.2f);
            s.ApplyCc(e, CcKind.Stun, 0.5f);
            Assert.AreEqual(AiState.Chase, e.State);
            Assert.AreEqual(0f, e.WarnT);
            T.Run(s, 0.45f);
            Assert.AreEqual(500f, s.Player.Hp, 0.01f);
        }

        [Test]
        public void LongRecoveryAfterSwing()
        {
            var (s, e) = Setup();
            s.Step(T.Dt, default);
            T.Run(s, 0.6f);
            Assert.AreEqual(AiState.Strike, e.State);
            T.Run(s, 1f);
            Assert.AreEqual(AiState.Recover, e.State, "PostAttackDuration 1.5 с");
        }
    }

    public class WitchTests
    {
        [Test]
        public void ShootsBoltWhenInSight()
        {
            var s = T.NewSim();
            var w = T.Spawn(s, EnemyKind.Witch, new Vector2(0f, 6f));
            w.Aggro = true; w.State = AiState.Peek; w.T = 3f;
            s.Step(T.Dt, default);
            Assert.AreEqual(AiState.Windup, w.State);
            T.Run(s, 0.55f);
            Assert.AreEqual(1, s.Bolts.Count, "сгусток после замаха 0.5 с");
            T.Run(s, 1.6f);
            Assert.AreEqual(500f - 50f, s.Player.Hp, 0.01f);
        }

        [Test]
        public void HidesBehindCover()
        {
            var s = T.NewSim();
            s.Obstacles.Add(new Obstacle { Pos = new Vector2(0f, 4f), R = 0.8f });
            var w = T.Spawn(s, EnemyKind.Witch, new Vector2(2f, 5f));
            w.Aggro = true; w.State = AiState.Cover; w.T = 0f;
            T.Run(s, 2.5f);
            Assert.Greater(w.Pos.y, 4.8f, "встаёт за камнем со стороны от Ace");
            Assert.IsFalse(s.LineOfSight(w.Pos, s.Player.Pos), "из укрытия Ace не видно");
        }
    }

    public class RoomTests
    {
        [Test]
        public void ThirtyRoomsOnlyFirstEnabled()
        {
            Assert.AreEqual(30, RoomLibrary.All.Count);
            Assert.AreEqual(1, RoomLibrary.Enabled.Count);
            Assert.AreEqual("A_Combat01", RoomLibrary.Enabled[0]);
        }

        [Test]
        public void EveryRoomLoadsWithHeroAndSpawnsOnFloor()
        {
            foreach (var name in RoomLibrary.All)
            {
                var room = RoomLibrary.Get(name);
                Assert.IsNotNull(room, name);
                Assert.Greater(room.floor.Length, 0, name);
                var s = new ArenaSim(1, room) { WavesEnabled = false };
                Assert.IsTrue(s.InsideRoom(s.Player.Pos, 0f), name + ": герой стоит на полу");
                int ok = 0;
                foreach (var sp in room.spawns) if (s.InsideRoom(sp.pos, 0f)) ok++;
                Assert.Greater(ok, room.spawns.Length / 2, name + ": точки появления на полу");
            }
        }

        [Test]
        public void WallsStopThePlayer()
        {
            var s = new ArenaSim(1, RoomLibrary.Get("A_Combat01")) { WavesEnabled = false };
            for (int i = 0; i < 4; i++)
            {
                var dir = new Vector2(Mathf.Cos(i * Mathf.PI / 2f + 0.3f), Mathf.Sin(i * Mathf.PI / 2f + 0.3f));
                s.Player.Pos = s.Room.heroStart;
                T.Run(s, 8f, new SimInput { Move = dir });
                Assert.IsTrue(s.InsideRoom(s.Player.Pos, 0f) || NearObstacle(s), "после 8 с бега в стену Ace всё ещё в комнате");
            }
        }

        static bool NearObstacle(ArenaSim s)
        {
            foreach (var o in s.Obstacles) if ((o.Pos - s.Player.Pos).magnitude < o.R + 1f) return true;
            return false;
        }

        [Test]
        public void FirstRoomRunsHadesIntroFight()
        {
            var s = new ArenaSim(1, RoomLibrary.Get("A_Combat01"));
            Assert.AreEqual(1, s.Wave);
            Assert.AreEqual(1, s.Enemies.Count, "волна 1: один Thug уже стоит в комнате");
            Assert.AreEqual(EnemyKind.Thug, s.Enemies[0].Kind);
            Assert.IsTrue(s.Enemies[0].PreSpawned);
            Assert.GreaterOrEqual((s.Enemies[0].Pos - s.Player.Pos).magnitude, Hades.U(750f) - 0.01f);
            Assert.AreEqual(1, s.Traps.Count);
            Assert.AreEqual(TrapKind.Spike, s.Traps[0].Kind);
        }

        [Test]
        public void SpikeTrapHitsWhoeverStandsOnIt()
        {
            var s = new ArenaSim(1, RoomLibrary.Get("A_Combat01")) { WavesEnabled = false };
            s.Enemies.Clear();
            var trap = s.Traps[0];
            s.Player.Pos = trap.Pos;
            s.Step(T.Dt, default);
            Assert.AreEqual(TrapPhase.Pre, trap.Phase, "шаг на площадку");
            T.Run(s, 0.25f);
            Assert.AreEqual(500f - 50f, s.Player.Hp, 0.01f, "5 урона Hades × 10 через 0.2 с");
        }
    }
}
