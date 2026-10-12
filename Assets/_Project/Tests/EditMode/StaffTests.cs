using Arena.Core;
using Arena.Sim;
using NUnit.Framework;
using UnityEngine;

namespace Arena.Tests
{
    /// <summary>Посох Мелинои из Hades II: тайминги и поведение четырёх скиллов (числа — Core/Staff.cs).</summary>
    public class StaffTests
    {
        const int Attack = 0, Special = 1, Cast = 2, Dash = 3;

        static ArenaSim NewSim()
        {
            var s = new ArenaSim(3) { WavesEnabled = false };
            s.Obstacles.Clear();
            s.Player.Pos = Vector2.zero;
            s.Player.Face = Vector2.up;
            return s;
        }

        static SimInput Hold(int slot, bool pressed = false, Vector2 move = default) =>
            new() { Held = 1 << slot, Pressed = pressed ? 1 << slot : 0, Move = move };

        static int Count(ArenaSim s, EffectKind kind, int phase = -1) =>
            s.Effects.FindAll(f => f.Kind == kind && !f.Done && (phase < 0 || f.Phase == phase)).Count;

        static Enemy Dummy(ArenaSim s, Vector2 pos)
        {
            var e = T.Spawn(s, EnemyKind.Wastrel, pos);
            e.Stun = 99f; // стоит и не бьёт
            return e;
        }

        [Test]
        public void DefaultSlotsAreStaffAndCardsStayInReserve()
        {
            var s = NewSim();
            Assert.AreEqual(CardId.StaffAttack, s.Slots[0].Main);
            Assert.AreEqual(CardId.StaffSpecial, s.Slots[1].Main);
            Assert.AreEqual(CardId.StaffCast, s.Slots[2].Main);
            Assert.AreEqual(CardId.StaffDash, s.Slots[3].Main);
            Assert.AreEqual(15, System.Array.FindAll(s.Reserve, c => c.HasValue).Length, "все 15 карт — в запасе");
        }

        [Test]
        public void FirstSwingLandsAfter60msAndHitsWithHadesDamage()
        {
            var s = NewSim();
            var e = Dummy(s, new Vector2(0f, 1.5f));
            T.Press(s, Attack);
            Assert.AreEqual(e.MaxHp, e.Hp, "ChargeTime 0.06 — ещё замах");
            T.Run(s, 0.2f);
            Assert.AreEqual(e.MaxHp - 20f * HadesScale.Hp, e.Hp, 0.5f, "Swing1: 20 урона Hades × 200/35");
        }

        [Test]
        public void ThreeHitComboThenStartsOver()
        {
            var s = NewSim();
            var e = Dummy(s, new Vector2(0f, 1.5f));
            float hp = e.Hp;
            float[] dmg = { 20f, 25f, 60f, 20f };
            float[] wait = { 0.3f, 0.3f, 0.5f, 0.3f }; // следующее нажатие — внутри ControlWindow (0.4 / 0.6 с от удара)
            for (int k = 0; k < 4; k++)
            {
                T.Press(s, Attack);
                T.Run(s, wait[k]);
                Assert.AreEqual(hp - dmg[k] * HadesScale.Hp, e.Hp, 0.5f, $"удар {k + 1}");
                hp = e.Hp;
            }
        }

        [Test]
        public void PressAfterControlWindowStartsComboOver()
        {
            var s = NewSim();
            var e = Dummy(s, new Vector2(0f, 1.5f));
            T.Press(s, Attack);
            T.Run(s, 0.5f); // окно Swing2 — 0.4 с от удара
            float hp = e.Hp;
            T.Press(s, Attack);
            T.Run(s, 0.3f);
            Assert.AreEqual(hp - 20f * HadesScale.Hp, e.Hp, 0.5f, "снова Swing1, а не Swing2");
        }

        [Test]
        public void SwingReaimsDuringWindupUntilFire()
        {
            var s = NewSim();
            var right = Dummy(s, new Vector2(1.6f, 0f));
            var up = Dummy(s, new Vector2(0f, 1.6f));
            var aim = new SimInput { UseMouse = true, HasCursor = true, Cursor = new Vector2(0f, 3f) };
            // доводим до третьего удара (замах 0.36 с), курсор вверх
            for (int k = 0; k < 2; k++) { aim.Pressed = 1; s.Step(T.Dt, aim); aim.Pressed = 0; for (int i = 0; i < 18; i++) s.Step(T.Dt, aim); }
            float hpRight = right.Hp;
            aim.Pressed = 1; s.Step(T.Dt, aim); aim.Pressed = 0;
            aim.Cursor = new Vector2(3f, 0f); // во время замаха — вправо
            for (int i = 0; i < 30; i++) s.Step(T.Dt, aim);
            Assert.AreEqual(hpRight - 60f * HadesScale.Hp, right.Hp, 0.5f, "SetCompleteAngleOnFire: удар туда, куда смотрел курсор в момент удара");
        }

        [Test]
        public void OmegaAttackAimFollowsCursorWhileCharging()
        {
            var s = NewSim();
            var right = Dummy(s, new Vector2(5f, 0f));
            var aim = new SimInput { UseMouse = true, HasCursor = true, Cursor = new Vector2(0f, 3f), Held = 1, Pressed = 1 };
            s.Step(T.Dt, aim); aim.Pressed = 0;
            for (int i = 0; i < 40; i++) s.Step(T.Dt, aim);
            Assert.AreEqual(StaffMove.OmegaAttack, s.Player.Staff.Move);
            aim.Cursor = new Vector2(3f, 0f);
            for (int i = 0; i < 50; i++) s.Step(T.Dt, aim);
            T.Run(s, 0.4f);
            Assert.AreEqual(right.MaxHp - 160f * HadesScale.Hp, right.Hp, 0.5f, "UseTargetAngle: волна ушла туда, куда довернули прицел");
        }

        [Test]
        public void SpecialChargesStandingStill()
        {
            var s = NewSim();
            s.Step(T.Dt, Hold(Special, true, Vector2.right));
            for (int i = 0; i < 20; i++) s.Step(T.Dt, Hold(Special, false, Vector2.right));
            Assert.AreEqual(0f, s.Player.Pos.x, 1e-4f, "BlockMoveInput: заряд спецатаки — стоя");
        }

        [Test]
        public void ComboResetsAfterControlWindow()
        {
            var s = NewSim();
            var e = Dummy(s, new Vector2(0f, 1.5f));
            T.Press(s, Attack);
            T.Run(s, 1.2f); // окно Swing2 (0.4 с после блока) прошло
            float hp = e.Hp;
            T.Press(s, Attack);
            T.Run(s, 0.3f);
            Assert.AreEqual(hp - 20f * HadesScale.Hp, e.Hp, 0.5f, "снова первый удар");
        }

        [Test]
        public void HoldingAttackChargesOmegaBothWays()
        {
            var s = NewSim();
            var front = Dummy(s, new Vector2(0f, 4f));
            var back = Dummy(s, new Vector2(0f, -4f));
            s.Step(T.Dt, Hold(Attack, true));
            for (int i = 0; i < 40; i++) s.Step(T.Dt, Hold(Attack));
            Assert.AreEqual(StaffMove.OmegaAttack, s.Player.Staff.Move, "после удара (0.06) и блока (0.23) — Ω-заряд 0.8 с");
            for (int i = 0; i < 50; i++) s.Step(T.Dt, Hold(Attack));
            T.Run(s, 0.4f);
            Assert.AreEqual(front.MaxHp - 160f * HadesScale.Hp, front.Hp, 0.5f, "волна вперёд");
            Assert.AreEqual(back.MaxHp - 160f * HadesScale.Hp, back.Hp, 0.5f, "и назад (ProjectileAngleOffset 180)");
        }

        [Test]
        public void ReleasingOmegaEarlyCancels()
        {
            var s = NewSim();
            s.Step(T.Dt, Hold(Attack, true));
            for (int i = 0; i < 40; i++) s.Step(T.Dt, Hold(Attack));
            Assert.AreEqual(StaffMove.OmegaAttack, s.Player.Staff.Move);
            s.Step(T.Dt, default);
            Assert.AreEqual(StaffMove.None, s.Player.Staff.Move);
            T.Run(s, 1f);
            Assert.AreEqual(0, Count(s, EffectKind.StaffWedge, 4));
        }

        [Test]
        public void SpecialTapFiresOnReleaseWithRecoil()
        {
            var s = NewSim();
            s.Step(T.Dt, Hold(Special, true));
            for (int i = 0; i < 10; i++) s.Step(T.Dt, Hold(Special));
            Assert.AreEqual(0, Count(s, EffectKind.StaffBall), "FireOnRelease: пока держишь — заряд");
            s.Step(T.Dt, default);
            Assert.AreEqual(1, Count(s, EffectKind.StaffBall, 0));
            T.Run(s, 0.3f);
            Assert.Less(s.Player.Pos.y, -0.1f, "отдача назад (SelfVelocity −610)");
        }

        [Test]
        public void HoldingSpecialGivesOmegaBall()
        {
            var s = NewSim();
            s.Step(T.Dt, Hold(Special, true));
            for (int i = 0; i < 45; i++) s.Step(T.Dt, Hold(Special));
            s.Step(T.Dt, default);
            Assert.AreEqual(1, Count(s, EffectKind.StaffBall, 1), "держали дольше 0.65 с — Ω-снаряд");
        }

        [Test]
        public void CastRootsEnemiesInside()
        {
            var s = NewSim();
            var e = T.Spawn(s, EnemyKind.Wastrel, new Vector2(0f, 2.5f));
            e.Stun = 0f;
            s.Player.DashInvuln = 99f;
            T.Press(s, Cast);
            T.Run(s, 0.5f);
            Assert.Greater(e.Rooted, 0f);
            var at = e.Pos;
            T.Run(s, 1f);
            Assert.Less((e.Pos - at).magnitude, 0.01f, "в круге враг не двигается");
        }

        [Test]
        public void OmegaCastExplodesWhenCircleExpires()
        {
            var s = NewSim();
            var e = Dummy(s, new Vector2(0f, 2f));
            s.Step(T.Dt, Hold(Cast, true));
            for (int i = 0; i < 80; i++) s.Step(T.Dt, Hold(Cast)); // 0.25 + 0.915 с
            s.Step(T.Dt, default);
            T.Run(s, 3f);
            Assert.AreEqual(e.MaxHp - 150f * HadesScale.Hp, e.Hp, 0.5f, "ArmedExpirationDamage 150");
        }

        [Test]
        public void CastWaitsForCircleToExpire()
        {
            var s = NewSim();
            T.Press(s, Cast);
            T.Run(s, 1f);
            var first = s.Player.Staff.Circle;
            Assert.IsNotNull(first);
            T.Press(s, Cast);
            T.Run(s, 0.5f);
            Assert.AreSame(first, s.Player.Staff.Circle, "ActiveProjectileCap 1: пока круг жив, нового нет");
            T.Run(s, 1.6f);
            T.Press(s, Cast);
            T.Run(s, 0.4f);
            Assert.AreNotSame(first, s.Player.Staff.Circle, "круг исчез — каст снова доступен");
        }

        [Test]
        public void OmegaWavesHitAnEnemyOnlyOnce()
        {
            var s = NewSim();
            var e = Dummy(s, new Vector2(0.2f, 0f));
            s.Step(T.Dt, Hold(Attack, true));
            for (int i = 0; i < 90; i++) s.Step(T.Dt, Hold(Attack));
            T.Run(s, 0.5f);
            float omega = 160f * HadesScale.Hp, swing = 20f * HadesScale.Hp;
            Assert.AreEqual(e.MaxHp - swing - omega, e.Hp, 0.5f, "ExcludeSameWaveHits: две волны — один удар");
        }

        [Test]
        public void BlinkCoversDistanceAndIsInvulnerable()
        {
            var s = NewSim();
            T.Press(s, Dash, Vector2.right);
            Assert.IsTrue(s.Player.Invulnerable);
            T.Run(s, 0.3f);
            Assert.AreEqual(320f * HadesScale.Unit, s.Player.Pos.x, 0.05f, "WeaponRange 320");
            s.Player.Face = Vector2.right;
            T.Press(s, Dash); // без направления — по взгляду
            T.Run(s, 0.3f);
            Assert.AreEqual(320f * HadesScale.Unit, s.Player.Pos.x, 0.05f, "ClipRegenInterval 0.74 — второй рывок ещё нельзя");
        }

        [Test]
        public void HoldingDashSprintsOnGamepad()
        {
            var s = NewSim();
            s.Player.Pos = new Vector2(-8f, 0f);
            s.Step(T.Dt, Hold(Dash, true, Vector2.right));
            for (int i = 0; i < 20; i++) s.Step(T.Dt, Hold(Dash, false, Vector2.right));
            Assert.AreEqual(StaffMove.Sprint, s.Player.Staff.Move);
            float x = s.Player.Pos.x;
            for (int i = 0; i < 60; i++) s.Step(T.Dt, Hold(Dash, false, Vector2.right));
            Assert.AreEqual(890f * HadesScale.Unit, s.Player.Pos.x - x, 0.05f, "SelfVelocityCap 890");
            s.Step(T.Dt, new SimInput { Move = Vector2.right });
            Assert.AreEqual(StaffMove.None, s.Player.Staff.Move, "отпустил — стоп");
        }

        [Test]
        public void AttackRightAfterDashIsDashStrike()
        {
            var s = NewSim();
            var e = Dummy(s, new Vector2(4f, 0f));
            T.Press(s, Dash, Vector2.right);
            T.Press(s, Attack); // во время рывка — в очередь (DashAttackQueue)
            T.Run(s, 0.6f);
            Assert.AreEqual(e.MaxHp - 30f * HadesScale.Hp, e.Hp, 0.5f, "удар из рывка: 30 урона Hades");
        }
    }
}
