using System.Linq;
using Arena.Core;
using Arena.Sim;
using NUnit.Framework;
using UnityEngine;

namespace Arena.Tests
{
    /// <summary>Особые эффекты тринкетов на уровне III, проклятия, Щит-оберег чаще, орбита сбивает вражеские снаряды.</summary>
    public class TrinketSpecialTests
    {
        static ArenaSim NewForms(params Trinket[] trinkets)
        {
            var s = new ArenaSim(5, RoomDef.Box(30f, 30f)) { WavesEnabled = false };
            s.EnableForms();
            s.RewardPending = false; s.Offers.Clear();
            s.Obstacles.Clear();
            s.Player.Pos = Vector2.zero;
            s.Player.Face = Vector2.up;
            s.Player.BuffInvuln = 999f;
            for (int i = 0; i < trinkets.Length; i++) s.TrinketSlots[i] = trinkets[i];
            s.RecomputeTrinkets();
            return s;
        }

        static Enemy Dummy(ArenaSim s, Vector2 pos, float hp = 0f)
        {
            var e = T.Spawn(s, EnemyKind.Wastrel, pos);
            e.State = AiState.Recover; e.T = 999f;
            if (hp > 0f) e.Hp = hp;
            return e;
        }

        static Trinket III(TrinketKind k) => new(k, 3);

        [Test]
        public void SpecialsWorkFromLevelThree()
        {
            var s = NewForms(new Trinket(TrinketKind.IceShard, 2));
            Assert.IsFalse(s.TrinketIII(TrinketKind.IceShard));
            s.TrinketSlots[0].Level = 3;
            Assert.IsTrue(s.TrinketIII(TrinketKind.IceShard));
            Assert.IsTrue(TrinketBook.All.All(k => TrinketBook.Group(k) == TrinketGroup.Curse ? TrinketBook.Curse(k, s.TrinketCfg).Length > 0 : TrinketBook.Special(k).Length > 0),
                "у каждого тринкета — особый эффект или плата");
        }

        [Test]
        public void IceShardFreezesOnSecondStack()
        {
            var s = NewForms(III(TrinketKind.IceShard));
            s.Forms[0].Card = new SkillCard(FormKind.Shot, Trait.Frost);
            var e = Dummy(s, new Vector2(0f, 3f), 99999f);
            T.Press(s, 0);
            T.Run(s, 0.5f);
            Assert.LessOrEqual(e.Frozen, 0f, "одно попадание — ещё не лёд");
            T.Press(s, 0);
            T.Run(s, 0.4f);
            Assert.Greater(e.Frozen, 0f, "второе — замерзает");
        }

        [Test]
        public void EmberSpreadsBurnFromBurningDead()
        {
            var s = NewForms(III(TrinketKind.Ember));
            var a = Dummy(s, new Vector2(0f, 3f));
            var b = Dummy(s, new Vector2(1.2f, 3f), 99999f);
            a.Burn = 50f; a.BurnT = 3f;
            s.Damage(a, 99999f, false);
            Assert.IsTrue(a.Dead);
            Assert.Greater(b.Burn, 0f, "сосед загорелся");
        }

        [Test]
        public void TimeSandKillRefundsCooldown()
        {
            var s = NewForms(III(TrinketKind.TimeSand));
            s.Forms[1].Card = new SkillCard(FormKind.Summon);
            s.Forms[1].Cd = 5f;
            var e = Dummy(s, new Vector2(0f, 3f));
            s.Damage(e, 99999f, false);
            Assert.AreEqual(5f - s.TrinketCfg.timeSandKill, s.Forms[1].Cd, 0.001f);
        }

        /// <summary>Колчан III: четвёртый выстрел — сразу очередь (4 нажатия — 5 снарядов).</summary>
        [Test]
        public void QuiverFourthShotIsVolley()
        {
            int Casts(ArenaSim s)
            {
                int n = 0;
                for (int k = 0; k < 4; k++)
                {
                    s.Step(T.Dt, new SimInput { Pressed = 1, Held = 1 });
                    foreach (var ev in s.Events) if (ev.Kind == EventKind.CardCast && ev.Value == 0) n++;
                    s.Step(T.Dt, new SimInput { Released = 1 });
                    foreach (var ev in s.Events) if (ev.Kind == EventKind.CardCast && ev.Value == 0) n++;
                    for (int j = 0; j < 40; j++)
                    {
                        s.Step(T.Dt, default);
                        foreach (var ev in s.Events) if (ev.Kind == EventKind.CardCast && ev.Value == 0) n++;
                    }
                }
                return n;
            }
            Assert.AreEqual(4, Casts(NewForms()));
            Assert.AreEqual(5, Casts(NewForms(III(TrinketKind.Quiver))));
        }

        [Test]
        public void HourglassChargedDashGoesTwoAndHalfTimes()
        {
            var s = NewForms(III(TrinketKind.Hourglass));
            T.Hold(s, ArenaSim.DashSlot, s.FormCfg.dashChargeTime + 0.05f); // стоя: рывок по стику — при отпускании
            Assert.AreEqual(0f, s.Player.Pos.x, 0.01f);
            T.Release(s, ArenaSim.DashSlot, new SimInput { Move = Vector2.right });
            T.Run(s, 0.8f);
            Assert.AreEqual(s.FormCfg.dashRange * 2.5f, s.Player.Pos.x, 0.15f);
        }

        [Test]
        public void CompassAllowsTwoZones()
        {
            var s = NewForms(III(TrinketKind.Compass));
            s.Forms[1].Card = new SkillCard(FormKind.Zone);
            T.Press(s, 1);
            T.Run(s, 0.6f);
            T.Press(s, 1);
            T.Run(s, 0.1f);
            Assert.AreEqual(2, s.Effects.Count(f => f.Kind == EffectKind.FormZone && !f.Done));
            T.Press(s, 1);
            T.Run(s, 0.1f);
            Assert.AreEqual(2, s.Effects.Count(f => f.Kind == EffectKind.FormZone && !f.Done), "третьей нет, пока стоят две");
        }

        [Test]
        public void WardReflectsAbsorbedHitAndRechargesFaster()
        {
            var s = NewForms(III(TrinketKind.Ward));
            Assert.Less(s.FormCfg.barrierCooldown, s.FormBase.barrierCooldown, "барьер перезаряжается быстрее");
            s.Player.BuffInvuln = 0f;
            s.Forms[2].Card = new SkillCard(FormKind.Barrier);
            var e = T.Spawn(s, EnemyKind.Wastrel, new Vector2(0f, 1.2f));
            T.Press(s, 2);
            Assert.IsFalse(s.HurtPlayer(50f, e.Pos));
            Assert.IsTrue(s.Effects.Any(f => f.Kind == EffectKind.FormBolt && f.Target == e && !f.Done), "удар отражён в ударившего");
        }

        [Test]
        public void SoulVesselAddsCellEveryTwoSouls()
        {
            var s = NewForms(III(TrinketKind.SoulVessel));
            var card = new SkillCard(FormKind.Shot, Trait.Fire);
            Assert.AreEqual(2, s.SoulCellsOf(card));
            card.Souls.Add(Trait.Frost); card.Souls.Add(Trait.Rot);
            Assert.AreEqual(3, s.SoulCellsOf(card), "две души — +1 ячейка");
            s.TrinketSlots[0].Level = 2;
            Assert.AreEqual(2, s.SoulCellsOf(card), "на II — как по редкости");
        }

        [Test]
        public void DoomSandFiresSooner()
        {
            Assert.AreEqual(2f, TrinketBook.Apply(new FormTuning(), new[] { III(TrinketKind.DoomSand) }, new TrinketTuning()).doomTime, 0.001f);
            Assert.AreEqual(3f, TrinketBook.Apply(new FormTuning(), new[] { new Trinket(TrinketKind.DoomSand, 2) }, new TrinketTuning()).doomTime, 0.001f);
        }

        // ---------- проклятия ----------

        [Test]
        public void GlassHeartHitsHarderAndTakesMore()
        {
            var s = NewForms(new Trinket(TrinketKind.GlassHeart));
            Assert.AreEqual(1.3f, TrinketBook.DamageMul(FormKind.Shot, s.TrinketSlots, s.TrinketCfg), 0.001f);
            s.Player.BuffInvuln = 0f;
            float hp = s.Player.Hp;
            Assert.IsTrue(s.HurtPlayer(20f, new Vector2(0f, 1f)));
            Assert.AreEqual(hp - 30f, s.Player.Hp, 0.01f, "урон по Смерти +50%");
        }

        [Test]
        public void EmptyHandWeakensShotStrengthensRest()
        {
            var t = new[] { new Trinket(TrinketKind.EmptyHand) };
            var c = new TrinketTuning();
            Assert.AreEqual(0.5f, TrinketBook.DamageMul(FormKind.Shot, t, c), 0.001f);
            Assert.AreEqual(1.25f, TrinketBook.DamageMul(FormKind.Zone, t, c), 0.001f);
            var s = NewForms(new Trinket(TrinketKind.EmptyHand));
            var e = Dummy(s, new Vector2(0f, 3f), 99999f);
            T.Press(s, 0);
            T.Run(s, 0.5f);
            Assert.AreEqual(s.FormCfg.shotDamage * 0.5f, 99999f - e.Hp, 1f);
        }

        [Test]
        public void BrokenMirrorSealsBarrierButSpeedsCooldowns()
        {
            var s = NewForms(new Trinket(TrinketKind.BrokenMirror));
            Assert.Less(s.FormCfg.summonCooldown, s.FormBase.summonCooldown);
            s.Forms[2].Card = new SkillCard(FormKind.Barrier);
            T.Press(s, 2);
            T.Run(s, 0.1f);
            Assert.IsFalse(s.Effects.Any(f => f.Kind == EffectKind.FormBarrier && !f.Done), "барьер не ставится");
        }

        // ---------- орбита ----------

        [Test]
        public void OrbitDestroysEnemyBolts()
        {
            var s = NewForms();
            s.Forms[0].Card = new SkillCard(FormKind.Shot, Trait.Orbit);
            T.Press(s, 0);
            var bolt = new Effect { Kind = EffectKind.EnemyBolt, Pos = new Vector2(-s.FormCfg.orbitRadius, 0f), Dir = Vector2.right, Speed = 0f, Life = 5f, R = 0.2f };
            s.Effects.Add(bolt);
            T.Run(s, 1.3f);
            Assert.IsTrue(bolt.Done, "снаряд на орбите сбил вражеский");
            Assert.Greater(s.Player.Hp, 0f);
        }
    }
}
