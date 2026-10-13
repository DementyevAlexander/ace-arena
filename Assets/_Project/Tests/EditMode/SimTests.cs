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
            var s = new ArenaSim(3, RoomDef.Box(30f, 30f)) { WavesEnabled = false };
            s.Obstacles.Clear();
            s.Player.Pos = Vector2.zero;
            return s;
        }

        public static void Hold(ArenaSim s, int slot, float seconds, SimInput extra = default)
        {
            var first = extra; first.Pressed = 1 << slot; first.Held = 1 << slot;
            s.Step(Dt, first);
            var held = extra; held.Held = 1 << slot;
            Run(s, seconds, held);
        }

        public static void Release(ArenaSim s, int slot, SimInput extra = default)
        {
            extra.Released = 1 << slot;
            s.Step(Dt, extra);
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
        public void AcceleratesToFullSpeedLikeZagreus()
        {
            var s = T.NewSim();
            s.Step(T.Dt, new SimInput { Move = new Vector2(1f, 0f) });
            Assert.AreEqual(s.PlayerCfg.acceleration * T.Dt, s.Player.Vel.x, 1e-4f, "Acceleration 2000: в первом кадре скорость ещё мала");
            T.Run(s, 0.3f, new SimInput { Move = new Vector2(1f, 0f) });
            Assert.AreEqual(4.1f, s.Player.Vel.magnitude, 1e-3f, "до полной скорости за 0.27 с");
        }

        [Test]
        public void StickTiltDoesNotChangeTopSpeed()
        {
            var s = T.NewSim();
            T.Run(s, 0.5f, new SimInput { Move = new Vector2(0.3f, 0f) });
            Assert.AreEqual(4.1f, s.Player.Vel.magnitude, 1e-3f);
        }

        [Test]
        public void StopsWithinAFewFrames()
        {
            var s = T.NewSim();
            T.Run(s, 0.5f, new SimInput { Move = Vector2.right });
            T.Run(s, 0.3f);
            float x = s.Player.Pos.x;
            s.Step(T.Dt, default);
            Assert.AreEqual(x, s.Player.Pos.x, 1e-5f);
        }

        [Test]
        public void KnockbackOverridesInputAboveThreshold()
        {
            var s = T.NewSim();
            s.HurtPlayer(10f, new Vector2(-1f, 0f), HitStop.None, 6f);
            s.Step(T.Dt, new SimInput { Move = Vector2.left });
            Assert.Greater(s.Player.Pos.x, 0f, "пока внешняя скорость выше 400 ед. Hades, ввод не тянет назад");
        }
    }

    public class AutoLockTests
    {
        [Test]
        public void NearestInsideArcWinsOverStraightAhead()
        {
            var s = T.NewSim();
            T.Spawn(s, EnemyKind.Stomper, new Vector2(0f, 5f));
            var near = T.Spawn(s, EnemyKind.Stomper, new Vector2(1f, 1.5f));
            Assert.AreSame(near, s.FindLockTarget(Vector2.up, 7.6f, 75f), "Hades: внутри угла решает расстояние");
        }

        [Test]
        public void OutsideArcFallsBackToNearestOverall()
        {
            var s = T.NewSim();
            var side = T.Spawn(s, EnemyKind.Stomper, new Vector2(3f, 0f));
            Assert.AreSame(side, s.FindLockTarget(Vector2.up, 7.6f, 75f), "никого в угле — ближайший вообще");
            T.Spawn(s, EnemyKind.Stomper, new Vector2(0f, 9f));
            Assert.AreSame(side, s.FindLockTarget(Vector2.up, 7.6f, 75f), "дальний вне радиуса — не кандидат");
        }

        [Test]
        public void OnlyFourNearestAreCandidates()
        {
            var s = T.NewSim();
            for (int i = 0; i < 4; i++) T.Spawn(s, EnemyKind.Stomper, new Vector2(-1.5f - i * 0.3f, -0.5f));
            T.Spawn(s, EnemyKind.Stomper, new Vector2(0f, 4f));
            Assert.AreNotEqual(new Vector2(0f, 4f), s.FindLockTarget(Vector2.up, 7.6f, 75f).Pos, "пятый (впереди, но дальше) не рассматривается");
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
        public void MouseShotsGoExactlyAtCursor()
        {
            var s = T.NewSim();
            T.Spawn(s, EnemyKind.Ghost, new Vector2(0f, 3f));
            var card = CardCatalog.Get(CardId.TwinDaggers);
            var dir = s.AimFor(card, new SimInput { UseMouse = true, HasCursor = true, Cursor = new Vector2(3f, 0f) });
            Assert.AreEqual(1f, dir.x, 1e-4f, "AutoLockWithMouse = false: никакого притягивания к врагу");
        }
    }

    public class CastTests
    {
        [Test]
        public void PinkPressDuringRecoveryIsBuffered()
        {
            var s = T.NewSim();
            s.SwapPlaces(0, 8); // Five of a Kind: 5 зарядов
            T.Press(s, 0);
            T.Run(s, 0.15f);
            Assert.IsNotNull(s.Player.Cast);
            T.Press(s, 0);
            Assert.AreEqual(0, s.Player.Buffer, "нажатие во время восстановления ждёт (RequestTriggerLock)");
            T.Run(s, 0.4f);
            Assert.AreEqual(-1, s.Player.Buffer);
        }

        [Test]
        public void BlueCancelsSoftRecoveryButNotHard()
        {
            var s = T.NewSim();
            T.Press(s, 0);
            // замах 0.1 с, потом жёсткое восстановление 0.1 с
            T.Run(s, 0.12f);
            Assert.AreEqual(CastPhase.Recovery, s.Player.Cast.Phase);
            T.Press(s, 3, Vector2.right);
            Assert.AreEqual(3, s.Player.Buffer, "жёсткое восстановление (Cancelable = false) не прерывается");
            s.Player.Buffer = -1;
            T.Run(s, 0.08f);
            Assert.AreEqual(CastPhase.Soft, s.Player.Cast.Phase);
            var before = s.Player.Pos;
            T.Press(s, 3, Vector2.right);
            Assert.Greater(s.Player.Pos.x - before.x, 2f, "мягкое восстановление прерывается синей картой");
        }

        [Test]
        public void PinkCastSlowsMovementButYellowRoots()
        {
            var s = T.NewSim();
            T.Run(s, 0.4f, new SimInput { Move = Vector2.right });
            T.Press(s, 0, Vector2.right);
            Assert.AreEqual(4.1f * 0.35f, s.Player.Vel.magnitude, 0.3f, "SwordWeapon: ×0.35 во время атаки");
            T.Run(s, 0.5f);
            T.Hold(s, 2, 0.3f, new SimInput { Move = Vector2.right });
            Assert.AreEqual(CastPhase.Aim, s.Player.Cast.Phase);
            Assert.AreEqual(0f, s.Player.Vel.magnitude, 1e-4f, "жёлтая карта в прицеле — на месте");
        }

        [Test]
        public void YellowAimsWithReticleAndFiresOnRelease()
        {
            var s = T.NewSim();
            var target = new Vector2(3f, 2f);
            var mouse = new SimInput { UseMouse = true, HasCursor = true, Cursor = target };
            T.Hold(s, 2, 0.5f, mouse);
            Assert.AreEqual(CastPhase.Aim, s.Player.Cast.Phase, "держишь — целишься");
            Assert.AreEqual(target, s.Player.Reticle);
            Assert.IsFalse(s.Effects.Exists(f => f.Kind == EffectKind.BlackHole));
            T.Release(s, 2, mouse);
            T.Run(s, 0.3f, mouse);
            var hole = s.Effects.Find(f => f.Kind == EffectKind.BlackHole);
            Assert.IsNotNull(hole, "отпустил — замах и выстрел");
            Assert.Less((hole.Pos - target).magnitude, 0.01f, "чёрная дыра — в точке прицела");
        }

        [Test]
        public void GamepadReticleStartsAheadAndMovesWithStick()
        {
            var s = T.NewSim();
            s.Player.Face = Vector2.up;
            T.Hold(s, 2, T.Dt);
            Assert.AreEqual(s.CastCfg.reticleStart, s.Player.Reticle.y, 0.01f, "ManualAimingInitialOffset 420");
            var y0 = s.Player.Reticle;
            T.Run(s, 0.1f, new SimInput { Held = 1 << 2, Move = Vector2.right });
            Assert.AreEqual(s.CastCfg.reticleSpeed * 0.1f, s.Player.Reticle.x - y0.x, 0.1f);
        }

        [Test]
        public void GettingHitCancelsAim()
        {
            var s = T.NewSim();
            T.Hold(s, 2, 0.2f);
            Assert.IsTrue(s.Player.Aiming);
            s.HurtPlayer(10f, Vector2.up);
            Assert.IsNull(s.Player.Cast, "CancelWeaponCharges при ударе");
        }

        [Test]
        public void BurstDamageGivesIFrames()
        {
            var s = T.NewSim();
            s.HurtPlayer(30f, Vector2.up);
            s.HurtPlayer(30f, Vector2.up);
            Assert.LessOrEqual(s.Player.HitInvuln, 0f, "60 < max(25% HP, 15 урона Hades ≈ 83) — неуязвимости ещё нет");
            s.HurtPlayer(30f, Vector2.up);
            Assert.AreEqual(1.3f, s.Player.HitInvuln, 1e-4f);
            Assert.IsFalse(s.HurtPlayer(30f, Vector2.up));
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
            var cards = System.Array.FindAll(CardCatalog.All, x => !ArenaSim.IsStaff(x.Id));
            Assert.AreEqual(15, cards.Length, "15 карт DandyAce (ещё 4 — приёмы посоха Мелинои)");
            Assert.AreEqual(19, CardCatalog.All.Length);
            foreach (var c in new[] { CardColor.Pink, CardColor.Yellow, CardColor.Blue })
                Assert.AreEqual(5, System.Array.FindAll(cards, x => x.Color == c).Length, c.ToString());
            for (int i = 0; i < CardCatalog.All.Length; i++) Assert.AreEqual(i, (int)CardCatalog.All[i].Id);
        }

        [Test]
        public void RosesChargesRechargeOneByOne()
        {
            var s = T.NewSim();
            s.SwapPlaces(0, 9); // Poisonous Roses из запаса в слот 1
            Assert.AreEqual(CardId.Roses, s.Slots[0].Main);
            T.Press(s, 0); T.Run(s, 0.3f);
            T.Press(s, 0); T.Run(s, 0.3f);
            T.Press(s, 0); T.Run(s, 0.05f);
            Assert.AreEqual(0, s.Slots[0].Charges);
            T.Run(s, 0.3f);
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
            T.Run(s, 0.8f);
            Assert.AreEqual(s.PlayerCfg.hp - 50f, s.Player.Hp, 0.01f);
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
            T.Run(s, 0.65f, new SimInput { Move = Vector2.right });
            Assert.AreEqual(s.PlayerCfg.hp, s.Player.Hp, 0.01f, "успел выйти из зоны до удара");
        }

        [Test]
        public void TeleportOutOfZoneAvoidsHit()
        {
            var (s, g) = Setup();
            T.Run(s, 0.3f);
            Assert.AreEqual(CardId.JumpScare, s.Slots[3].Main);
            T.Press(s, 3, Vector2.right);
            T.Run(s, 0.4f);
            Assert.AreEqual(s.PlayerCfg.hp, s.Player.Hp, 0.01f);
            Assert.Greater(s.Player.Pos.x, 3f);
        }

        [Test]
        public void StunCancelsTheStrike()
        {
            var (s, g) = Setup();
            T.Run(s, 0.2f);
            s.ApplyCc(g, CcKind.Stun, 1f);
            Assert.IsNull(g.Tele);
            T.Run(s, 0.8f);
            Assert.AreEqual(s.PlayerCfg.hp, s.Player.Hp, 0.01f);
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
            Assert.AreEqual(s.PlayerCfg.hp - 59f, s.Player.Hp, 0.01f);
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
            Assert.AreEqual(s.PlayerCfg.hp, s.Player.Hp, 0.01f);
        }
    }
}
