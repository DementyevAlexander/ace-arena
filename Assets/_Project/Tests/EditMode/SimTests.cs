using Arena.Core;
using Arena.Sim;
using NUnit.Framework;
using UnityEngine;

namespace Arena.Tests
{
    static class T
    {
        public const float Dt = 1f / 60f;

        public static ArenaSim NewSim()
        {
            var s = new ArenaSim(3) { WavesEnabled = false };
            s.Obstacles.Clear();
            s.Player.Pos = Vector2.zero;
            return s;
        }

        public static Enemy Spawn(ArenaSim s, EnemyKind kind, Vector2 pos, AiState state = AiState.Chase)
        {
            var e = s.SpawnEnemy(kind, pos);
            e.State = state; e.T = 0f; e.Cd = 0f; e.SpawnInvuln = 0f;
            return e;
        }

        public static void Run(ArenaSim s, float seconds, SimInput input = default)
        {
            int n = Mathf.RoundToInt(seconds / Dt);
            for (int i = 0; i < n; i++) s.Step(Dt, input);
        }

        public static void Press(ArenaSim s, int slot, Vector2 move = default)
        {
            s.Step(Dt, new SimInput { Pressed = 1 << slot, Held = 1 << slot, Move = move });
            s.Step(Dt, new SimInput { Released = 1 << slot, Move = move });
        }
    }

    public class MovementTests
    {
        [Test]
        public void SpeedIsFullAtOnceAndIgnoresStickTilt()
        {
            var s = T.NewSim();
            s.Step(T.Dt, new SimInput { Move = new Vector2(0.3f, 0f) });
            Assert.AreEqual(4.1f * T.Dt, s.Player.Pos.x, 1e-4f, "первый же кадр — полная скорость, наклон 0.3 её не уменьшает");
            T.Run(s, 1f, new SimInput { Move = new Vector2(1f, 0f) });
            Assert.AreEqual(4.1f * (1f + T.Dt), s.Player.Pos.x, 0.02f);
        }

        [Test]
        public void StopsInstantly()
        {
            var s = T.NewSim();
            T.Run(s, 0.5f, new SimInput { Move = Vector2.right });
            float x = s.Player.Pos.x;
            s.Step(T.Dt, default);
            Assert.AreEqual(x, s.Player.Pos.x, 1e-5f);
        }
    }

    public class AutoLockTests
    {
        [Test]
        public void SmallestAngleWinsOverDistance()
        {
            var s = T.NewSim();
            var far = T.Spawn(s, EnemyKind.Stomper, new Vector2(0f, 5f));
            T.Spawn(s, EnemyKind.Stomper, new Vector2(1f, 1.5f));
            Assert.AreSame(far, s.FindLockTarget(Vector2.up, 8.3f, 75f));
        }

        [Test]
        public void OutsideArcOrRangeIsIgnored()
        {
            var s = T.NewSim();
            T.Spawn(s, EnemyKind.Stomper, new Vector2(3f, 0f));   // 90° от прицела
            T.Spawn(s, EnemyKind.Stomper, new Vector2(0f, 9f));   // дальше 8.3 до края тела
            Assert.IsNull(s.FindLockTarget(Vector2.up, 8.3f, 75f));
            Assert.IsNotNull(s.FindLockTarget(Vector2.up, 8.3f, 100f), "при ±100° боковой враг уже кандидат");
        }

        [Test]
        public void MouseLocksEnemyUnderCursor()
        {
            var s = T.NewSim();
            var e = T.Spawn(s, EnemyKind.Ghost, new Vector2(4f, -2f));
            var card = CardCatalog.Get(CardId.TwinDaggers);
            var dir = s.AimFor(card, new SimInput { UseMouse = true, HasCursor = true, Cursor = e.Pos + new Vector2(0.2f, 0.1f) });
            Assert.AreSame(e, s.Player.Lock);
            Assert.Greater(Vector2.Dot(dir, e.Pos.normalized), 0.999f);
        }

        [Test]
        public void GamepadLockPersistsWithNeutralStick()
        {
            var s = T.NewSim();
            var a = T.Spawn(s, EnemyKind.Stomper, new Vector2(0f, 4f), AiState.Recover);
            var card = CardCatalog.Get(CardId.TwinDaggers);
            s.AimFor(card, new SimInput { Move = Vector2.up });
            Assert.AreSame(a, s.Player.Lock);
            var b = T.Spawn(s, EnemyKind.Stomper, new Vector2(-4f, 0f), AiState.Recover);
            s.AimFor(card, default);
            Assert.AreSame(a, s.Player.Lock, "стик отпущен — цель держится");
            s.AimFor(card, new SimInput { Move = Vector2.left });
            Assert.AreSame(b, s.Player.Lock, "стик отклонён — цель ищется заново");
        }
    }

    public class CardTests
    {
        [Test]
        public void UpgradeDependsOnMainColor()
        {
            Assert.AreEqual(UpgradeKind.BonusVsStun, Upgrades.For(CardId.Dazzling, CardId.TwinDaggers).Kind);
            var onDash = Upgrades.For(CardId.Dazzling, CardId.Dash);
            Assert.AreEqual(UpgradeKind.Stun, onDash.Kind);
            Assert.AreEqual(0.5f, onDash.Duration, 1e-4f, "цикл Dashing Dash 0.5-");
            Assert.AreEqual(2.5f, Upgrades.For(CardId.Dazzling, CardId.BlackHole).Duration, 1e-4f, "цикл Black Hole 8");
            Assert.AreEqual(UpgradeKind.BonusVsCc, Upgrades.For(CardId.BlackHole, CardId.Roses).Kind);
            Assert.AreEqual(UpgradeKind.Pull, Upgrades.For(CardId.BlackHole, CardId.Starburst).Kind);
        }

        [Test]
        public void FifteenCardsFivePerColor()
        {
            Assert.AreEqual(15, CardCatalog.All.Length);
            foreach (CardColor c in System.Enum.GetValues(typeof(CardColor)))
                Assert.AreEqual(5, System.Array.FindAll(CardCatalog.All, x => x.Color == c).Length, c.ToString());
            for (int i = 0; i < CardCatalog.All.Length; i++) Assert.AreEqual(i, (int)CardCatalog.All[i].Id);
        }

        [Test]
        public void RosesChargesRechargeOneByOne()
        {
            var s = T.NewSim();
            s.SwapPlaces(0, 9); // Poisonous Roses из запаса в слот 1
            Assert.AreEqual(CardId.Roses, s.Slots[0].Main);
            for (int i = 0; i < 3; i++) { T.Press(s, 0); T.Run(s, 0.15f); }
            Assert.AreEqual(0, s.Slots[0].Charges);
            T.Run(s, 0.4f);
            Assert.AreEqual(1, s.Slots[0].Charges, "первый заряд — через 0.75 с после первого броска");
            T.Run(s, 0.75f);
            Assert.AreEqual(2, s.Slots[0].Charges);
        }

        [Test]
        public void TwinDaggersHitTwiceAndBleed()
        {
            var s = T.NewSim();
            var e = T.Spawn(s, EnemyKind.Stomper, new Vector2(0f, 3f));
            e.Stun = 5f;
            T.Press(s, 0, Vector2.zero);
            T.Run(s, 1.2f);
            Assert.LessOrEqual(e.Hp, 725f - 200f);
            Assert.IsTrue(e.Dots.Exists(d => d.Kind == DotKind.Bleed));
        }

        [Test]
        public void PinkUpgradeBonusOnlyVsStunned()
        {
            var s = T.NewSim();
            s.SwapPlaces(4, 11); // Dazzling Display под Twin Daggers → Prey Seeker
            Assert.AreEqual(UpgradeKind.BonusVsStun, s.Slots[0].Upgrade.Kind);
            var e = T.Spawn(s, EnemyKind.Ghost, new Vector2(0f, 3f));
            e.Stun = 5f;
            var src = new HitSource { Slot = s.Slots[0], Card = s.Slots[0].Card, Up = s.Slots[0].Upgrade, Base = 100f, Div = 2f };
            s.HitEnemy(e, src);
            Assert.AreEqual(975f - 100f - 112.5f, e.Hp, 0.6f, "+225 / 2 хитбокса");
        }
    }

    public class GhostTests
    {
        static (ArenaSim, Enemy) Setup()
        {
            var s = T.NewSim();
            s.Player.Pos = new Vector2(0f, 0.6f);
            var g = T.Spawn(s, EnemyKind.Ghost, new Vector2(0f, 2.5f));
            return (s, g);
        }

        [Test]
        public void StandingInZoneGetsHit()
        {
            var (s, g) = Setup();
            T.Run(s, 0.05f);
            Assert.AreEqual(AiState.Windup, g.State);
            Assert.IsTrue(g.Tele.Contains(s.Player.Pos, s.PlayerCfg.radius));
            T.Run(s, 0.65f);
            Assert.AreEqual(500f - 50f, s.Player.Hp, 0.01f);
        }

        [Test]
        public void ReactingImmediatelyEscapesFromZoneCenter()
        {
            var (s, g) = Setup();
            s.Step(T.Dt, default); // замах начался в этом шаге
            Assert.AreEqual(AiState.Windup, g.State);
            var center = g.Tele.Center;
            s.Player.Pos = center; // самый неудобный случай — центр зоны
            T.Run(s, 0.2f);        // реакция 0.2 с
            T.Run(s, 0.5f, new SimInput { Move = Vector2.right });
            Assert.AreEqual(500f, s.Player.Hp, 0.01f, "успел выйти из зоны до удара");
        }

        [Test]
        public void TeleportOutOfZoneAvoidsHit()
        {
            var (s, g) = Setup();
            T.Run(s, 0.3f);
            Assert.AreEqual(CardId.JumpScare, s.Slots[3].Main);
            T.Press(s, 3, Vector2.right);
            T.Run(s, 0.4f);
            Assert.AreEqual(500f, s.Player.Hp, 0.01f);
            Assert.Greater(s.Player.Pos.x, 3f);
        }

        [Test]
        public void StunCancelsTheStrike()
        {
            var (s, g) = Setup();
            T.Run(s, 0.2f);
            s.ApplyCc(g, CcKind.Stun, 1f);
            Assert.IsNull(g.Tele);
            T.Run(s, 0.6f);
            Assert.AreEqual(500f, s.Player.Hp, 0.01f);
        }
    }

    public class StomperTests
    {
        [Test]
        public void NoticesChasesTelegraphsAndLunges()
        {
            var s = T.NewSim();
            var e = T.Spawn(s, EnemyKind.Stomper, new Vector2(0f, 3.5f), AiState.Wander);
            e.T = 5f; e.Dir = Vector2.zero;
            s.Step(T.Dt, default);
            Assert.AreEqual(AiState.Alert, e.State);
            T.Run(s, 0.5f);
            Assert.AreEqual(AiState.Chase, e.State);
            for (int i = 0; i < 300 && e.State != AiState.Windup; i++) s.Step(T.Dt, default);
            Assert.AreEqual(AiState.Windup, e.State);
            Assert.AreEqual(TeleShape.Lane, e.Tele.Shape);
            T.Run(s, 0.9f);
            Assert.AreEqual(500f - 59f, s.Player.Hp, 0.01f);
        }

        [Test]
        public void SidestepDodgesTheLunge()
        {
            var s = T.NewSim();
            var e = T.Spawn(s, EnemyKind.Stomper, new Vector2(0f, 1.6f));
            s.Step(T.Dt, default);
            Assert.AreEqual(AiState.Windup, e.State);
            T.Run(s, 0.25f);
            T.Run(s, 0.6f, new SimInput { Move = Vector2.right });
            Assert.AreEqual(500f, s.Player.Hp, 0.01f);
        }
    }
}
