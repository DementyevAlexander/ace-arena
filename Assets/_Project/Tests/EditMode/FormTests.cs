using Arena.Core;
using Arena.Sim;
using NUnit.Framework;
using UnityEngine;

namespace Arena.Tests
{
    /// <summary>Режим форм: карточки, слияние, снаряд факела, свойства на формах, барьер, рывок, выдача карточек.</summary>
    public class FormTests
    {
        static ArenaSim NewForms()
        {
            var s = new ArenaSim(5, RoomDef.Box(30f, 30f)) { WavesEnabled = false };
            s.EnableForms();
            s.RewardPending = false; s.Offers.Clear();
            s.Obstacles.Clear();
            s.Player.Pos = Vector2.zero;
            s.Player.Face = Vector2.up;
            s.Player.BuffInvuln = 999f;
            return s;
        }

        static void Hold(ArenaSim s, int slot, float seconds)
        {
            s.Step(T.Dt, new SimInput { Pressed = 1 << slot, Held = 1 << slot });
            T.Run(s, seconds, new SimInput { Held = 1 << slot });
            s.Step(T.Dt, new SimInput { Released = 1 << slot });
        }

        static void Tap(ArenaSim s, int slot) => T.Press(s, slot);

        static Enemy Dummy(ArenaSim s, Vector2 pos, EnemyKind kind = EnemyKind.Wastrel)
        {
            var e = T.Spawn(s, kind, pos);
            e.State = AiState.Recover; e.T = 999f; // стоит и не бьёт
            return e;
        }

        [Test]
        public void StartsWithShotAndDashAndOffersThreeCards()
        {
            var s = new ArenaSim(5);
            s.EnableForms();
            Assert.AreEqual(FormKind.Shot, s.Forms[0].Card.Form);
            Assert.AreEqual(FormKind.Dash, s.Forms[ArenaSim.DashSlot].Card.Form);
            Assert.IsNull(s.Forms[1].Card);
            Assert.IsTrue(s.RewardPending);
            Assert.AreEqual(3, s.Offers.Count);
            foreach (var o in s.Offers) Assert.IsTrue(o.Own.HasValue);
        }

        [Test]
        public void DashCardsGoOnlyToDashSlotAndFusionHasTwoCells()
        {
            var s = new ArenaSim(5);
            s.EnableForms();
            s.Offers.Clear();
            s.Offers.Add(new SkillCard(FormKind.Dash, Trait.Fire));
            s.Offers.Add(new SkillCard(FormKind.Zone, Trait.Frost));
            Assert.IsTrue(s.CanTake(0, ArenaSim.DashSlot));
            Assert.IsFalse(s.CanTake(0, 1));
            Assert.IsTrue(s.CanTake(1, 1));
            Assert.IsFalse(s.CanTake(1, ArenaSim.DashSlot));
            s.FuseOffer(1, 0);
            Assert.IsFalse(s.RewardPending);
            Assert.AreEqual(1, s.Forms[0].Card.Level(Trait.Frost));
            Assert.AreEqual(FormKind.Shot, s.Forms[0].Card.Form, "форма остаётся от карты слота");
            s.Forms[0].Card.Souls.Add(Trait.Frost);
            Assert.AreEqual(2, s.Forms[0].Card.Level(Trait.Frost), "одинаковые души складываются в уровень");
            Assert.IsFalse(s.Forms[0].Card.CanFuse, "две ячейки заполнены");
        }

        [Test]
        public void ShotFiresLikeTorchesAndHits()
        {
            var s = NewForms();
            var e = Dummy(s, new Vector2(0f, 3f));
            float hp = e.Hp;
            int casts = 0;
            s.Step(T.Dt, new SimInput { Pressed = 1, Held = 1 });
            for (int i = 0; i < Mathf.RoundToInt(1.05f / T.Dt); i++)
            {
                s.Step(T.Dt, new SimInput { Held = 1 });
                foreach (var ev in s.Events) if (ev.Kind == EventKind.CardCast) casts++;
            }
            Assert.AreEqual(3, casts, "первый выстрел через 0.25 с, дальше каждые 0.38 с: 0.25, 0.63, 1.01");
            Assert.Less(e.Hp, hp - 30f * HadesScale.Hp * 1.5f, "попадания по 30 урона Hades");
        }

        [Test]
        public void TapFiresOneShot()
        {
            var s = NewForms();
            int casts = 0;
            s.Step(T.Dt, new SimInput { Pressed = 1, Held = 1 });
            for (int i = 0; i < 60; i++)
            {
                s.Step(T.Dt, default);
                foreach (var ev in s.Events) if (ev.Kind == EventKind.CardCast) casts++;
            }
            Assert.AreEqual(1, casts);
        }

        [Test]
        public void FireShotBurnsOverTime()
        {
            var s = NewForms();
            s.Forms[0].Card = new SkillCard(FormKind.Shot, Trait.Fire);
            var e = Dummy(s, new Vector2(0f, 2.5f));
            Tap(s, 0);
            T.Run(s, 0.6f);
            Assert.Greater(e.Burn, 0f);
            float hp = e.Hp;
            T.Run(s, 3.5f);
            Assert.AreEqual(0f, e.Burn, 0.01f);
            Assert.Less(e.Hp, hp - 100f, "Scorch выгорает уроном");
        }

        [Test]
        public void PierceShotPassesThroughEnemies()
        {
            var s = NewForms();
            s.Forms[0].Card = new SkillCard(FormKind.Shot, Trait.Pierce);
            var a = Dummy(s, new Vector2(0f, 2f));
            var b = Dummy(s, new Vector2(0f, 3.5f));
            float ha = a.Hp, hb = b.Hp;
            Tap(s, 0);
            T.Run(s, 0.9f);
            Assert.Less(a.Hp, ha);
            Assert.Less(b.Hp, hb);
        }

        [Test]
        public void RicochetJumpsToNextEnemy()
        {
            var s = NewForms();
            s.Forms[0].Card = new SkillCard(FormKind.Shot, Trait.Ricochet);
            var a = Dummy(s, new Vector2(0f, 2.5f));
            var b = Dummy(s, new Vector2(2.5f, 3f));
            float hb = b.Hp;
            Tap(s, 0);
            T.Run(s, 1.2f);
            Assert.Less(b.Hp, hb, "отскок к ближайшему врагу");
        }

        [Test]
        public void SplitFiresTwoShots()
        {
            var s = NewForms();
            s.Forms[0].Card = new SkillCard(FormKind.Shot, Trait.Split);
            Tap(s, 0);
            T.Run(s, 0.3f);
            int n = 0;
            foreach (var f in s.Effects) if (f.Kind == EffectKind.FormShot && !f.Done) n++;
            Assert.AreEqual(2, n);
        }

        [Test]
        public void ZoneDamagesAndSlows()
        {
            var s = NewForms();
            s.Forms[1].Card = new SkillCard(FormKind.Zone, Trait.Fire);
            var e = Dummy(s, new Vector2(0f, 3.5f));
            float hp = e.Hp;
            Tap(s, 1);
            T.Run(s, 1.2f);
            Assert.Less(e.Hp, hp);
            Assert.Greater(e.ZoneSlowT, 0f);
            Assert.Greater(e.Burn + 0.01f, 0f);
            Assert.Greater(s.Forms[1].Cd, 1f, "новая зона — когда прежняя исчезнет");
        }

        [Test]
        public void ZoneDoomLinksDamage()
        {
            var s = NewForms();
            s.Forms[1].Card = new SkillCard(FormKind.Zone, Trait.Doom);
            var a = Dummy(s, new Vector2(-0.5f, 3.5f));
            var b = Dummy(s, new Vector2(0.5f, 3.5f));
            var far = Dummy(s, new Vector2(6f, -4f));
            Tap(s, 1);
            T.Run(s, 0.2f);
            Assert.Greater(a.LinkT, 0f);
            float hb = b.Hp, hf = far.Hp;
            s.Damage(a, 300f, false);
            Assert.AreEqual(hb - 300f * s.FormCfg.linkShare, b.Hp, 1f, "Hitch: 30% урона — связанным");
            Assert.AreEqual(hf, far.Hp, 0.01f, "кто не в зоне — не связан");
        }

        [Test]
        public void BarrierAbsorbsWastrelHits()
        {
            var s = NewForms();
            s.Player.BuffInvuln = 0f;
            s.Forms[2].Card = new SkillCard(FormKind.Barrier, Trait.Frost);
            var e = T.Spawn(s, EnemyKind.Wastrel, new Vector2(0f, 1.2f));
            Tap(s, 2);
            float hp = s.Player.Hp;
            Assert.IsFalse(s.HurtPlayer(50f, e.Pos), "урон не прошёл");
            Assert.AreEqual(hp, s.Player.Hp, 0.01f, "барьер поглотил удар");
            Assert.Greater(e.Frozen, 0f, "Иней на барьере: ударивший замерзает");
        }

        [Test]
        public void PierceDashHitsAndReapRestoresDash()
        {
            var s = NewForms();
            s.Forms[ArenaSim.DashSlot].Card = new SkillCard(FormKind.Dash, Trait.Pierce);
            s.Forms[ArenaSim.DashSlot].Card.Souls.Add(Trait.Reap);
            var e = Dummy(s, new Vector2(1.2f, 0f));
            e.Hp = 10f;
            s.Step(T.Dt, new SimInput { Pressed = 1 << 3, Held = 1 << 3, Move = Vector2.right });
            T.Run(s, 0.3f);
            Assert.IsTrue(e.Dead, "рывок с Пронзанием бьёт задетых");
            Assert.LessOrEqual(s.Forms[ArenaSim.DashSlot].Cd, 0f, "Жатва: добивание рывком сразу восстанавливает рывок");
        }

        [Test]
        public void PlainDashIsInvulnerableAndHasHadesCooldown()
        {
            var s = NewForms();
            s.Player.BuffInvuln = 0f;
            s.Step(T.Dt, new SimInput { Pressed = 1 << 3, Held = 1 << 3, Move = Vector2.right });
            Assert.IsTrue(s.Player.Invulnerable);
            T.Run(s, 0.25f);
            Assert.AreEqual(320f * HadesScale.Unit, s.Player.Pos.x, 0.05f, "WeaponRange 320");
            Assert.Greater(s.Forms[ArenaSim.DashSlot].Cd, 0.6f, "ClipRegenInterval 0.74 с конца рывка");
        }

        [Test]
        public void FrostFreezesOnThirdHitAndRotSpreads()
        {
            var s = NewForms();
            var e = Dummy(s, new Vector2(0f, 3f));
            var n = Dummy(s, new Vector2(1f, 3f));
            var card = new SkillCard(FormKind.Shot, Trait.Frost);
            card.Souls.Add(Trait.Rot);
            s.Forms[0].Card = card;
            Hold(s, 0, 1.2f);
            T.Run(s, 0.3f);
            Assert.Greater(e.Frozen, 0f, "третье попадание замораживает");
            Assert.Greater(e.RotT, 0f);
            s.Damage(e, 99999f, false);
            Assert.Greater(n.RotT, 0f, "умерший с гнилью заражает соседей");
        }

        [Test]
        public void RewardComesAfterEveryThirdWave()
        {
            var s = new ArenaSim(7, RoomLibrary.Get("N_Combat06"));
            s.EnableForms();
            s.Player.BuffInvuln = 999f;
            s.TakeOffer(0, s.Offers[0].Form == FormKind.Dash ? ArenaSim.DashSlot : 1);
            Assert.IsFalse(s.RewardPending);
            int rewards = 0;
            for (int i = 0; i < 60 * 60 && rewards < 2; i++)
            {
                s.Step(T.Dt, default);
                foreach (var e in s.Enemies) if (e.Alive) s.Damage(e, 99999f, false);
                if (s.RewardPending)
                {
                    rewards++;
                    Assert.AreEqual(s.ArenaCfg.wavesPerReward * rewards, s.Wave, "карточка — после каждой третьей волны");
                    s.FuseOffer(0, 0);
                }
                // набор волн комнаты пройден — шаг в дверь, дальше следующая комната
                if (s.DoorsOpen && !s.RewardPending) s.Player.Pos = s.Room.exits[0].pos;
            }
            Assert.AreEqual(2, rewards);
        }
    }
}
