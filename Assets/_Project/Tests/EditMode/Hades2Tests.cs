using Arena.Core;
using Arena.Sim;
using NUnit.Framework;
using UnityEngine;

namespace Arena.Tests
{
    public class HeroAndWaveTests
    {
        [Test]
        public void InvulnerabilityNeedsAQuarterOfHpWithinSixTenths()
        {
            var s = T.NewSim();
            s.HurtPlayer(50f, Vector2.up);
            Assert.LessOrEqual(s.Player.HitInvuln, 0f, "один удар Wastrel (9 из 30 HP) неуязвимости не даёт");
            T.Run(s, 0.3f);
            s.HurtPlayer(39f, Vector2.up);
            Assert.Greater(s.Player.HitInvuln, 1f, "50 + 39 за 0.6 с ≥ 15 урона Hades — 1.3 с неуязвимости");
        }
    }

    /// <summary>Wastrel (Brawler) из Hades II: комбо выпад-трапеция → дуга.</summary>
    public class WastrelTests
    {
        static (ArenaSim, Enemy) Setup()
        {
            var s = T.NewSim();
            s.Player.Pos = new Vector2(0f, 0.6f);
            var g = T.Spawn(s, EnemyKind.Wastrel, new Vector2(0f, 2.5f));
            return (s, g);
        }

        [Test]
        public void FirstHitIsLungeTrapezoidThenArc()
        {
            var (s, g) = Setup();
            T.Run(s, 0.05f);
            Assert.AreEqual(AiState.Windup, g.State);
            Assert.AreEqual(TeleShape.Trapezoid, g.Tele.Shape);
            Assert.IsTrue(g.Tele.Contains(s.Player.Pos, s.PlayerCfg.radius));
            T.Run(s, 1.15f + 0.25f);
            Assert.AreEqual(s.PlayerCfg.hp - 50f, s.Player.Hp, 0.01f, "удар 1 после замаха 1.15 с");
            T.Run(s, 0.2f); // FireDuration 0.4 с, PostAttackDuration 0
            Assert.AreEqual(2, g.Strike);
            Assert.AreEqual(TeleShape.Fan, g.Tele.Shape, "сразу за ним — дуга BrawlerMeleeClose");
        }

        [Test]
        public void SidestepDuringWindupEscapes()
        {
            var (s, g) = Setup();
            s.Step(T.Dt, default);
            Assert.AreEqual(AiState.Windup, g.State);
            T.Run(s, 0.3f);
            T.Run(s, 0.8f, new SimInput { Move = Vector2.right });
            T.Run(s, 0.3f);
            Assert.AreEqual(s.PlayerCfg.hp, s.Player.Hp, 0.01f, "трапеция не следит за целью — шаг в сторону спасает");
        }

        [Test]
        public void TeleportOutOfZoneAvoidsHit()
        {
            var (s, g) = Setup();
            T.Run(s, 0.3f);
            Assert.AreEqual(CardId.JumpScare, s.Slots[3].Main);
            T.Press(s, 3, Vector2.right);
            T.Run(s, 1.1f);
            Assert.AreEqual(s.PlayerCfg.hp, s.Player.Hp, 0.01f);
            Assert.Greater(s.Player.Pos.x, 3f);
        }

        [Test]
        public void StunCancelsTheStrike()
        {
            var (s, g) = Setup();
            T.Run(s, 0.2f);
            s.ApplyCc(g, CcKind.Stun, 2f);
            Assert.IsNull(g.Tele);
            T.Run(s, 1.5f);
            Assert.AreEqual(s.PlayerCfg.hp, s.Player.Hp, 0.01f);
        }

        [Test]
        public void StunnedWindupIsRetriedFromWhereHeStands()
        {
            var (s, g) = Setup();
            T.Run(s, 0.3f);
            s.ApplyCc(g, CcKind.Stun, 0.5f);
            T.Run(s, 0.55f);
            Assert.AreEqual(AiState.Windup, g.State, "после оглушения — снова замах удара 1, без подхода");
            Assert.AreEqual(1, g.Strike);
        }

        [Test]
        public void WandersUntilPlayerComesNear()
        {
            var s = T.NewSim();
            var g = T.Spawn(s, EnemyKind.Wastrel, new Vector2(0f, 7f), AiState.Wander);
            T.Spawn(s, EnemyKind.Wastrel, new Vector2(-9f, 6.5f), AiState.Wander); // не последний живой (иначе AggroIfLastAlive)
            T.Run(s, 2f);
            Assert.IsFalse(g.Aggro, "дальше AIAggroRange 4.55 — бродит");
            Assert.Less((g.Pos - new Vector2(0f, 7f)).magnitude, 3.2f, "бродит случайным шагом от точки появления");
            s.Player.Pos = g.Pos + new Vector2(0f, -3f);
            T.Run(s, 0.25f);
            Assert.IsTrue(g.Aggro, "заметил с задержкой реакции 0.05–0.2 с");
        }
    }

    /// <summary>Casket (Mage) из Hades II: прячется за укрытием, выходит на линию огня, три самонаводящихся снаряда.</summary>
    public class CasketTests
    {
        static int Bolts(ArenaSim s) => s.Effects.FindAll(f => f.Kind == EffectKind.EnemyBolt && !f.Done).Count;

        [Test]
        public void HidesThenShootsThreeHomingBolts()
        {
            var s = T.NewSim();
            var e = T.Spawn(s, EnemyKind.Casket, new Vector2(0f, 6f));
            s.Step(T.Dt, default);
            Assert.AreEqual(AiState.Hide, e.State, "сначала — в укрытие (TakeCoverDuration 1.5–2.2 с)");
            for (int i = 0; i < 200 && e.State != AiState.Windup; i++) s.Step(T.Dt, default);
            Assert.AreEqual(AiState.Windup, e.State, "в пределах 7.97 и с прямой видимостью — замах");
            Assert.AreEqual(TeleShape.Lane, e.Tele.Shape);
            T.Run(s, 1f + 0.4f);
            Assert.AreEqual(3, Bolts(s), "FireTicks 3 через 0.17 с");
            Assert.Greater(e.Pos.y, 6f, "каждый выстрел отталкивает назад (FireTickSelfVelocity)");
        }

        [Test]
        public void BoltsHomeInAndSlowWithoutKnockback()
        {
            var s = T.NewSim();
            var e = T.Spawn(s, EnemyKind.Casket, new Vector2(0f, 6f));
            for (int i = 0; i < 200 && e.State != AiState.Windup; i++) s.Step(T.Dt, default);
            T.Run(s, 1.02f);
            // отходим вбок: снаряды доворачивают следом
            bool slowed = false;
            for (int i = 0; i < 150; i++)
            {
                s.Step(T.Dt, new SimInput { Move = i < 20 ? Vector2.right : Vector2.zero });
                if (s.Player.Slow > 0f) slowed = true;
            }
            Assert.Less(s.Player.Hp, s.PlayerCfg.hp, "самонаводящийся снаряд догнал");
            Assert.AreEqual(0f, Mathf.Repeat(s.PlayerCfg.hp - s.Player.Hp, s.CasketCfg.damage), 0.01f);
            Assert.IsTrue(slowed, "HeroOnHitStun: замедление × 0.7 на 0.2 с");
        }

        [Test]
        public void HidesBehindCoverAwayFromDeath()
        {
            var s = T.NewSim();
            s.Obstacles.Add(new Obstacle { Pos = new Vector2(3f, 3f), R = 0.7f });
            var e = T.Spawn(s, EnemyKind.Casket, new Vector2(4f, 6f));
            T.Run(s, 1.4f);
            var behind = (Vector2.one * 3f) - s.Player.Pos;
            Assert.Greater(Vector2.Dot(e.Pos - new Vector2(3f, 3f), behind.normalized), 1.5f, "встал за колонной с дальней от Смерти стороны");
        }

        [Test]
        public void PausesAfterNoticing()
        {
            var s = T.NewSim();
            var e = T.Spawn(s, EnemyKind.Casket, new Vector2(0f, 8f), AiState.Wander);
            T.Run(s, 0.25f);
            Assert.IsTrue(e.Aggro, "AIAggroRange 9.49");
            Assert.AreEqual(AiState.Alert, e.State, "AggroDuration 0.7 с");
            T.Run(s, 0.75f);
            Assert.AreEqual(AiState.Hide, e.State);
        }
    }
}
