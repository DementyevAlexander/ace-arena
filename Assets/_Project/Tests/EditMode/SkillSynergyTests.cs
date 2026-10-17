using System.Linq;
using Arena.Core;
using Arena.Sim;
using NUnit.Framework;
using UnityEngine;

namespace Arena.Tests
{
    /// <summary>Свойства из Noita и Nova Drift (Наведение, Орбита, Спуск), связки двух свойств на карте, стартовый набор в хабе.</summary>
    public class SkillSynergyTests
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

        static Enemy Dummy(ArenaSim s, Vector2 pos, float hp = 0f)
        {
            var e = T.Spawn(s, EnemyKind.Wastrel, pos);
            e.State = AiState.Recover; e.T = 999f; // стоит и не бьёт
            if (hp > 0f) e.Hp = hp;
            return e;
        }

        static SkillCard Card(FormKind form, Trait own, params Trait[] souls)
        {
            var c = new SkillCard(form, own, Rarity.Legendary);
            c.Souls.AddRange(souls);
            return c;
        }

        // ---------- Наведение ----------

        /// <summary>Веер из четырёх снарядов: без Наведения крайние проходят мимо, с ним — доворачивают в цель.</summary>
        [Test]
        public void HomingBendsFanIntoTarget()
        {
            float Dealt(bool homing)
            {
                var s = NewForms();
                s.Forms[0].Card = homing ? Card(FormKind.Shot, Trait.Homing, Trait.Split, Trait.Split, Trait.Split) : Card(FormKind.Shot, Trait.Split, Trait.Split, Trait.Split);
                var e = Dummy(s, new Vector2(0f, 4.5f), 99999f);
                T.Press(s, 0);
                T.Run(s, 1f);
                return 99999f - e.Hp;
            }
            float plain = Dealt(false), homed = Dealt(true);
            Assert.Greater(homed, plain + s_shot * 0.9f, $"с Наведением попало больше снарядов веера: {homed} против {plain}");
        }

        static readonly float s_shot = new FormTuning().shotDamage;

        [Test]
        public void HomingDashTurnsTowardEnemyAhead()
        {
            var s = NewForms();
            s.Forms[ArenaSim.DashSlot].Card = new SkillCard(FormKind.Dash, Trait.Homing);
            Dummy(s, new Vector2(2f, 1.2f));
            T.Press(s, ArenaSim.DashSlot, Vector2.right);
            T.Run(s, 0.3f);
            Assert.Greater(s.Player.Pos.y, 0.6f, "рывок вправо довернул к врагу впереди (31°)");
        }

        // ---------- Орбита ----------

        [Test]
        public void OrbitShotCirclesDeathAndHitsAgain()
        {
            var s = NewForms();
            s.Forms[0].Card = new SkillCard(FormKind.Shot, Trait.Orbit);
            var e = Dummy(s, new Vector2(1.6f, 0f), 99999f);
            T.Press(s, 0);
            T.Run(s, 0.3f);
            var shot = s.Effects.FirstOrDefault(f => f.Kind == EffectKind.FormShot && !f.Done);
            Assert.IsNotNull(shot, "снаряд на орбите ещё жив");
            Assert.AreEqual(s.FormCfg.orbitRadius, (shot.Pos - s.Player.Pos).magnitude, 0.05f, "кружит на радиусе орбиты");
            T.Run(s, 1.2f);
            Assert.GreaterOrEqual(99999f - e.Hp, s_shot * 1.9f, "за круг снова задел того же врага");
        }

        [Test]
        public void OrbitZoneIsAuraAroundDeath()
        {
            var s = NewForms();
            s.Forms[1].Card = new SkillCard(FormKind.Zone, Trait.Orbit);
            T.Press(s, 1);
            T.Run(s, 0.6f, new SimInput { Move = Vector2.right });
            var z = s.Effects.First(f => f.Kind == EffectKind.FormZone && !f.Done);
            Assert.Greater(s.Player.Pos.x, 1f, "Смерть ушла с места");
            Assert.Less((z.Pos - s.Player.Pos).magnitude, 0.01f, "зона идёт за ней");
        }

        [Test]
        public void OrbitDashLeavesShards()
        {
            var s = NewForms();
            s.Forms[ArenaSim.DashSlot].Card = new SkillCard(FormKind.Dash, Trait.Orbit);
            T.Press(s, ArenaSim.DashSlot, Vector2.right);
            T.Run(s, 0.3f);
            int shards = s.Effects.Count(f => f.Kind == EffectKind.FormShot && !f.Done && f.Phase == 3 && f.As == FormKind.Dash);
            Assert.AreEqual(2, shards, "после рывка — осколки на орбите (1 + уровень)");
        }

        // ---------- Спуск ----------

        /// <summary>Снаряд со Спуском при попадании применяет карту следующего слота (зону) в цель — перезарядку зоны не трогает.</summary>
        [Test]
        public void TriggerShotCastsNextSlotAtTarget()
        {
            var s = NewForms();
            s.Forms[0].Card = new SkillCard(FormKind.Shot, Trait.Trigger);
            s.Forms[1].Card = new SkillCard(FormKind.Zone);
            var e = Dummy(s, new Vector2(0f, 3f), 99999f);
            T.Press(s, 0);
            T.Run(s, 0.4f);
            var z = s.Effects.FirstOrDefault(f => f.Kind == EffectKind.FormZone && !f.Done);
            Assert.IsNotNull(z, "зона следующего слота появилась");
            Assert.IsTrue(z.Payload);
            Assert.Less((z.Pos - e.Pos).magnitude, 0.6f, "в точке попадания");
            Assert.LessOrEqual(s.Forms[1].Cd, 0f, "своя зона — без перезарядки");
            Assert.IsFalse(s.Forms[1].Active.Contains(z));
        }

        /// <summary>Применённое Спуском дальше не спускает: снаряд следующего слота тоже со Спуском, но цепочки нет.</summary>
        [Test]
        public void TriggerDoesNotChain()
        {
            var s = NewForms();
            s.Forms[0].Card = new SkillCard(FormKind.Shot, Trait.Trigger);
            s.Forms[1].Card = new SkillCard(FormKind.Shot, Trait.Trigger);
            Dummy(s, new Vector2(0f, 3f), 99999f);
            var b = Dummy(s, new Vector2(2.5f, 3f), 99999f);
            T.Press(s, 0);
            T.Run(s, 0.45f);
            Assert.Greater(s.Forms[0].TriggerLock, 0f, "ЛКМ спустил");
            T.Run(s, 0.6f);
            Assert.Less(b.Hp, 99999f, "снаряд ПКМ долетел до второго врага");
            Assert.LessOrEqual(s.Forms[1].TriggerLock, 0f, "но сам дальше не спустил");
        }

        // ---------- Связки ----------

        [Test]
        public void BondsArePairsOfDifferentTraitsWithoutRepeats()
        {
            var pairs = Bonds.All.Select(Bonds.Pair).ToList();
            Assert.IsTrue(pairs.All(p => p.a != p.b));
            Assert.AreEqual(pairs.Count, pairs.Select(p => p.a < p.b ? (p.a, p.b) : (p.b, p.a)).Distinct().Count(), "одна пара — одна связка");
            Assert.AreEqual(Bonds.All.Length, Bonds.All.Select(Bonds.Name).Distinct().Count());
        }

        [Test]
        public void FusingSecondTraitOpensBond()
        {
            var c = new SkillCard(FormKind.Shot, Trait.Fire);
            CollectionAssert.AreEqual(new[] { Bond.Napalm }, Bonds.Opens(c, Trait.Blast));
            Assert.IsEmpty(Bonds.Of(c));
            c.Souls.Add(Trait.Blast);
            CollectionAssert.Contains(Bonds.Of(c), Bond.Napalm);
            Assert.IsEmpty(Bonds.Opens(c, Trait.Blast), "уже открыта");
            Assert.IsTrue(FormText.Describe(c).Any(l => l.Contains("Напалм")), "видна в описании карты");
        }

        /// <summary>Напалм: взрыв поджигает соседа, которого сам снаряд не задел.</summary>
        [Test]
        public void NapalmBlastIgnitesNeighbours()
        {
            float NeighbourBurn(bool napalm)
            {
                var s = NewForms();
                s.Forms[0].Card = napalm ? Card(FormKind.Shot, Trait.Blast, Trait.Fire) : Card(FormKind.Shot, Trait.Blast);
                Dummy(s, new Vector2(0f, 3f), 99999f);
                var n = Dummy(s, new Vector2(1f, 3.2f), 99999f);
                T.Press(s, 0);
                T.Run(s, 0.4f);
                return n.Burn;
            }
            Assert.AreEqual(0f, NeighbourBurn(false), 0.001f);
            Assert.Greater(NeighbourBurn(true), 0f);
        }

        /// <summary>Цепная детонация: добитый взрывается и добивает соседа, тот — следующего, до которого первый взрыв не достаёт.</summary>
        [Test]
        public void ChainDetonationPropagates()
        {
            var s = NewForms();
            s.Forms[0].Card = Card(FormKind.Shot, Trait.Blast, Trait.Reap);
            Dummy(s, new Vector2(0f, 3f), 10f);
            Dummy(s, new Vector2(1.3f, 3f), 10f);
            var far = Dummy(s, new Vector2(2.6f, 3f), 99999f);
            T.Press(s, 0);
            T.Run(s, 0.8f);
            Assert.Less(far.Hp, 99999f, "третий задет только цепочкой");
        }

        /// <summary>Сверхпроводник: перескок молнии по заиндевевшему — вдвое сильнее.</summary>
        [Test]
        public void SuperconductorDoublesLightningOnFrosted()
        {
            float Jump(bool bond)
            {
                var s = NewForms();
                s.Forms[0].Card = bond ? Card(FormKind.Shot, Trait.Lightning, Trait.Frost) : Card(FormKind.Shot, Trait.Lightning);
                Dummy(s, new Vector2(0f, 3f), 99999f);
                var b = Dummy(s, new Vector2(1.5f, 3f), 99999f);
                b.Frost = 1; b.FrostT = 99f;
                T.Press(s, 0);
                T.Run(s, 0.4f);
                return 99999f - b.Hp;
            }
            float plain = Jump(false), bonded = Jump(true);
            Assert.Greater(plain, 0f);
            Assert.AreEqual(plain * 2f, bonded, plain * 0.05f);
        }

        /// <summary>Двойной приговор: после первого срабатывания — второе через 1 с.</summary>
        [Test]
        public void DoubleDoomFiresAgain()
        {
            var s = NewForms();
            s.Forms[0].Card = Card(FormKind.Shot, Trait.Doom, Trait.Echo);
            var e = Dummy(s, new Vector2(0f, 3f), 99999f);
            T.Press(s, 0);
            T.Run(s, 3.3f); // эхо-снаряд попал под приговор, приговор сработал
            Assert.Greater(e.DoomEchoDmg, 0f, "второй приговор ждёт");
            float hp = e.Hp;
            T.Run(s, 1.2f);
            Assert.AreEqual(0f, e.DoomEchoDmg, 0.001f);
            Assert.Less(e.Hp, hp, "и сработал");
        }

        // ---------- Разгон, Дальнобой, Поглощение ----------

        /// <summary>Разгон снаряда: после 4 ю пробега выстрел на 60% сильнее, стоя — как обычно.</summary>
        [Test]
        public void KineticRunUpBoostsShot()
        {
            float Dealt(float run)
            {
                var s = NewForms();
                s.Forms[0].Card = new SkillCard(FormKind.Shot, Trait.Kinetic);
                s.Forms[0].Run = run;
                var e = Dummy(s, new Vector2(0f, 3f), 99999f);
                T.Press(s, 0);
                T.Run(s, 0.5f);
                Assert.AreEqual(0f, s.Forms[0].Run, 0.05f, "выстрел тратит разбег");
                return 99999f - e.Hp;
            }
            float still = Dealt(0f), ran = Dealt(4f);
            Assert.AreEqual(s_shot, still, s_shot * 0.02f);
            Assert.AreEqual(still * 1.6f, ran, still * 0.03f);
        }

        [Test]
        public void KineticDashRamsByDistance()
        {
            var s = NewForms();
            s.Forms[ArenaSim.DashSlot].Card = new SkillCard(FormKind.Dash, Trait.Kinetic);
            var e = Dummy(s, new Vector2(1.8f, 0f), 99999f);
            T.Press(s, ArenaSim.DashSlot, Vector2.right);
            T.Run(s, 0.3f);
            float dealt = 99999f - e.Hp;
            Assert.Greater(dealt, s.FormCfg.kineticRam * 0.3f, "таран: урон за пройденный путь");
            Assert.Less(dealt, s.FormCfg.kineticRam * 2.4f);
        }

        [Test]
        public void SnipeShotHitsHarderFarAway()
        {
            float Dealt(float y)
            {
                var s = NewForms();
                s.Forms[0].Card = new SkillCard(FormKind.Shot, Trait.Snipe);
                var e = Dummy(s, new Vector2(0f, y), 99999f);
                T.Press(s, 0);
                T.Run(s, 0.8f);
                return 99999f - e.Hp;
            }
            float near = Dealt(1.5f), far = Dealt(5f);
            Assert.Greater(far, near * 1.35f, $"издалека сильнее: {far} против {near}");
        }

        /// <summary>Поглощение: добивание лечит, но до следующей награды — не больше 20% здоровья.</summary>
        [Test]
        public void SiphonHealsUpToCapPerReward()
        {
            var s = NewForms();
            float max = s.PlayerCfg.hp;
            s.Player.Hp = max * 0.5f;
            s.Forms[0].Card = Card(FormKind.Shot, Trait.Siphon, Trait.Pierce);
            for (int i = 0; i < 16; i++) Dummy(s, new Vector2(0f, 1.4f + 0.3f * i), 1f);
            T.Press(s, 0);
            T.Run(s, 1f);
            Assert.Greater(s.Kills, 10, "снаряд прошёл сквозь строй");
            Assert.AreEqual(max * (0.5f + s.FormCfg.siphonRoomCap), s.Player.Hp, max * 0.005f, "16 × 2% упёрлись в лимит 20%");
        }

        [Test]
        public void SiphonBarrierHealsPerAbsorbedHit()
        {
            var s = NewForms();
            s.Player.BuffInvuln = 0f;
            float max = s.PlayerCfg.hp;
            s.Player.Hp = max * 0.5f;
            s.Forms[2].Card = new SkillCard(FormKind.Barrier, Trait.Siphon);
            var e = T.Spawn(s, EnemyKind.Wastrel, new Vector2(0f, 1.2f));
            T.Press(s, 2);
            Assert.IsFalse(s.HurtPlayer(50f, e.Pos), "удар поглощён");
            Assert.AreEqual(max * (0.5f + s.FormCfg.siphonBarrier), s.Player.Hp, 0.01f);
        }

        // ---------- переброс наград ----------

        /// <summary>У пятачка — до трёх перебросов за выдачу: прежние карточки сгорают, новые вырастают заново; четвёртого нет.</summary>
        [Test]
        public void RerollReplacesLootThreeTimesPerDrop()
        {
            var s = new ArenaSim(7, RoomLibrary.Get("A_Combat01"));
            s.EnableForms();
            s.Player.BuffInvuln = 999f;
            for (int i = 0; i < 60 * 120 && s.Loot.Count == 0; i++)
            {
                s.Step(T.Dt, default);
                foreach (var e in s.Enemies) if (e.Alive) s.Damage(e, 99999f, false);
            }
            Assert.AreEqual(2, s.Loot.Count, "пятачок с картами");
            Assert.AreEqual(s.ArenaCfg.rerollsPerDrop, s.RerollsLeft);
            foreach (var f in s.Effects) if (f.Kind == EffectKind.Grace) f.Done = true; // ромбики с врагов не мешают счёту
            s.AddGrace(3 * s.ArenaCfg.rerollCost + 5 - s.Grace); // переброс — за Милость
            Assert.IsFalse(s.RerollLoot(), "пока карточки вырастают — нельзя");
            for (int r = 0; r < 3; r++)
            {
                for (int k = 0; k < 120 && s.Time < s.LootReadyAt; k++) s.Step(T.Dt, default);
                s.Player.Pos = s.Loot[0].Pos;
                Assert.IsTrue(s.CanReroll);
                var old = s.Loot.Select(g => g.Card).ToList();
                Assert.IsTrue(s.RerollLoot());
                Assert.AreEqual(2, s.Loot.Count);
                Assert.IsTrue(s.Loot.All(g => g.Card != null && !old.Contains(g.Card)), "новые карты");
                Assert.AreNotEqual(s.Loot[0].Card.Own, s.Loot[1].Card.Own, "свойства разные");
                Assert.IsTrue(s.Events.Exists(ev => ev.Kind == EventKind.LootBurned), "прежние сгорели");
                Assert.Greater(s.LootReadyAt, s.Time, "новые вырастают");
            }
            for (int k = 0; k < 120 && s.Time < s.LootReadyAt; k++) s.Step(T.Dt, default);
            Assert.AreEqual(0, s.RerollsLeft);
            Assert.AreEqual(5, s.Grace, "три переброса по 15");
            Assert.IsFalse(s.RerollLoot(), "четвёртого переброса нет");
            Assert.IsTrue(s.PickUpLoot(), "взять можно как обычно");
        }

        // ---------- выпадение и стартовый набор ----------

        [Test]
        public void NewTraitsDropOnPedestal()
        {
            var s = NewForms();
            var own = Enumerable.Range(0, 4000).Select(_ => s.RollLootCard().Own).ToList();
            foreach (var t in new[] { Trait.Homing, Trait.Orbit, Trait.Trigger })
                Assert.IsTrue(own.Contains(t), $"{FormText.Name(t)} выпадает");
        }
    }
}
