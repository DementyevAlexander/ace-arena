using System.Linq;
using Arena.Core;
using Arena.Sim;
using NUnit.Framework;
using UnityEngine;

namespace Arena.Tests
{
    /// <summary>Тринкеты (docs/trinkets.md): слоты, замена, уровни до V, итоговые числа форм, Сосуд душ, веса выпадения карт,
    /// предложения у мини-босса.</summary>
    public class TrinketTests
    {
        static ArenaSim NewForms()
        {
            var s = new ArenaSim(5);
            s.EnableForms();
            s.Player.BuffInvuln = 999f;
            return s;
        }

        /// <summary>Положить тринкет на пятачок под ноги и подобрать — открывается выбор тринкета.</summary>
        static void Offer(ArenaSim s, TrinketKind kind, int level = 1)
        {
            s.Loot.Clear();
            s.Loot.Add(new GroundCard { Trinket = new Trinket(kind, level), Pos = s.Player.Pos });
            s.LootReadyAt = 0f;
            Assert.IsTrue(s.PickUpLoot(), "подобрать тринкет");
            Assert.IsTrue(s.RewardPending);
            Assert.IsNotNull(s.TrinketOffer);
            Assert.AreEqual(0, s.Offers.Count, "карт в выборе нет");
        }

        [Test]
        public void PutLevelUpToFiveAndReplace()
        {
            var s = NewForms();
            float burn = s.FormBase.burn, shot = s.FormBase.shotDamage;
            Assert.IsTrue(s.TrinketSlots.All(t => t == null), "в начале слоты пусты");

            Offer(s, TrinketKind.Ember);
            Assert.IsFalse(s.TrinketOfferIsLevelUp);
            s.PutTrinket(0);
            Assert.IsFalse(s.RewardPending);
            Assert.IsNull(s.TrinketOffer);
            Assert.AreEqual(TrinketKind.Ember, s.TrinketSlots[0].Kind);
            Assert.AreEqual(burn * 1.4f, s.FormCfg.burn, 0.01f, "I: +40%");

            // повтор — уровень, слот не нужен
            int[] mul = { 1, 2, 2, 3, 4 };
            for (int lvl = 2; lvl <= TrinketBook.MaxLevel; lvl++)
            {
                Offer(s, TrinketKind.Ember);
                Assert.IsTrue(s.TrinketOfferIsLevelUp);
                Assert.IsFalse(s.CanPutTrinket(1), "такой же не ставится во второй слот");
                s.LevelTrinket();
                Assert.AreEqual(lvl, s.TrinketSlots[0].Level);
                Assert.AreEqual(burn * (1f + 0.4f * mul[lvl - 1]), s.FormCfg.burn, 0.01f, $"уровень {lvl}");
            }
            Offer(s, TrinketKind.Ember);
            Assert.IsFalse(s.CanLevelTrinket, "выше V не поднять");
            s.LeaveLoot();
            Assert.IsNull(s.TrinketOffer);

            // замена: прежний пропадает вместе с уровнем
            Offer(s, TrinketKind.Quiver);
            s.PutTrinket(1);
            Assert.AreEqual(shot * 1.15f, s.FormCfg.shotDamage, 0.01f);
            Offer(s, TrinketKind.Compass);
            s.PutTrinket(2);
            Offer(s, TrinketKind.Bell);
            Assert.IsTrue(s.CanPutTrinket(1), "слоты заняты — можно заменить");
            s.PutTrinket(1);
            Assert.AreEqual(TrinketKind.Bell, s.TrinketSlots[1].Kind);
            Assert.AreEqual(shot, s.FormCfg.shotDamage, 0.01f, "Колчан заменён — урон снаряда базовый");
            Assert.AreEqual(s.FormBase.summonLife + 2f, s.FormCfg.summonLife, 0.01f);
            Assert.AreEqual(s.FormBase.burn, s.FormBase.burn, 0f, "база не меняется");

            // новый забег — слоты пусты, числа базовые
            s.Restart();
            Assert.IsTrue(s.TrinketSlots.All(t => t == null));
            Assert.AreEqual(burn, s.FormCfg.burn, 0.01f);
        }

        /// <summary>Каждый тринкет на I меняет своё число, на V — по множителю 4. Перезарядка не короче четверти базовой.</summary>
        [Test]
        public void ApplyChangesNumbersByLevel()
        {
            var b = new FormTuning();
            var c = new TrinketTuning();
            FormTuning At(TrinketKind k, int lvl) => TrinketBook.Apply(b, new[] { new Trinket(k, lvl) }, c);
            Assert.AreEqual(b.freezeTime + 0.5f, At(TrinketKind.IceShard, 1).freezeTime, 0.001f);
            Assert.AreEqual(b.chainTargets + 4, At(TrinketKind.LightningRod, 5).chainTargets);
            Assert.AreEqual(b.rotVuln + 0.4f, At(TrinketKind.RotBone, 5).rotVuln, 0.001f);
            Assert.AreEqual(b.doomFraction + 0.15f, At(TrinketKind.DoomSand, 1).doomFraction, 0.001f);
            Assert.AreEqual(b.ricochetBounces + 2, At(TrinketKind.Mirror, 3).ricochetBounces, "III чисел не прибавляет: как II");
            Assert.AreEqual(b.echoFraction + 0.3f, At(TrinketKind.TuningFork, 2).echoFraction, 0.001f);
            Assert.AreEqual(b.blastRadius * 2f, At(TrinketKind.PowderHorn, 5).blastRadius, 0.001f);
            Assert.AreEqual(b.dashCooldown * 0.85f, At(TrinketKind.Feather, 1).dashCooldown, 0.001f);
            Assert.AreEqual(b.dashCooldown * 0.4f, At(TrinketKind.Feather, 5).dashCooldown, 0.001f);
            Assert.AreEqual(b.zoneRadius * 1.15f, At(TrinketKind.Compass, 1).zoneRadius, 0.001f);
            Assert.AreEqual(b.barrierHits + 4, At(TrinketKind.Ward, 5).barrierHits);
            var h = At(TrinketKind.Hourglass, 1);
            Assert.AreEqual(b.shotChargeWindow + 0.1f, h.shotChargeWindow, 0.001f);
            Assert.AreEqual(b.barrierChargeWindow + 0.1f, h.barrierChargeWindow, 0.001f);
            var ts = At(TrinketKind.TimeSand, 5);
            Assert.AreEqual(b.summonCooldown * 0.68f, ts.summonCooldown, 0.001f);
            Assert.AreEqual(b.shotInterval * 0.68f, ts.shotInterval, 0.001f);

            c.feather = 1f; // −100% — упирается в пол
            Assert.AreEqual(b.dashCooldown * c.minCooldownMul, At(TrinketKind.Feather, 1).dashCooldown, 0.001f);
        }

        /// <summary>Сосуд душ: каждая влитая душа — +5% урона карты на I, +20% на V; без душ — без прибавки.</summary>
        [Test]
        public void SoulVesselScalesWithSouls()
        {
            var c = new TrinketTuning();
            var card = new SkillCard(FormKind.Shot, Trait.Fire, Rarity.Epic);
            var one = new[] { new Trinket(TrinketKind.SoulVessel) };
            Assert.AreEqual(1f, TrinketBook.SoulDamageMul(card, one, c), 0.001f);
            card.Souls.Add(Trait.Frost);
            card.Souls.Add(Trait.Frost);
            Assert.AreEqual(1.1f, TrinketBook.SoulDamageMul(card, one, c), 0.001f);
            Assert.AreEqual(1.4f, TrinketBook.SoulDamageMul(card, new[] { new Trinket(TrinketKind.SoulVessel, 5) }, c), 0.001f);
            Assert.AreEqual(1f, TrinketBook.SoulDamageMul(card, new Trinket[] { null, null }, c), 0.001f);
        }

        /// <summary>Тринкет направляет выпадение: Уголёк — Пламя ×3 (1/18 → 3/20), Колчан — снаряды ×2 (1/4.6 → 2/5.6).</summary>
        [Test]
        public void TrinketsSteerCardDrops()
        {
            const int N = 12000;
            float Share(ArenaSim s, System.Func<SkillCard, bool> f)
            {
                int n = 0;
                for (int i = 0; i < N; i++) if (f(s.RollLootCard())) n++;
                return n / (float)N;
            }
            var a = NewForms();
            Assert.AreEqual(1f / Traits.Count, Share(a, x => x.Own == Trait.Fire), 0.012f, "без тринкета");
            Assert.AreEqual(1f / 4.6f, Share(a, x => x.Form == FormKind.Shot), 0.015f);
            a.TrinketSlots[0] = new Trinket(TrinketKind.Ember);
            a.TrinketSlots[1] = new Trinket(TrinketKind.Quiver);
            Assert.AreEqual(3f / (Traits.Count + 2), Share(a, x => x.Own == Trait.Fire), 0.015f, "Уголёк");
            Assert.AreEqual(2f / 5.6f, Share(a, x => x.Form == FormKind.Shot), 0.015f, "Колчан");
        }

        /// <summary>Предложения: разные виды; стоящий не на V — всегда среди них; стоящий на V — никогда.</summary>
        [Test]
        public void OffersIncludeOwnedUnlessMaxed()
        {
            var s = NewForms();
            for (int i = 0; i < 200; i++)
            {
                var o = s.RollTrinketOffers(3, 1);
                Assert.AreEqual(3, o.Count);
                Assert.AreEqual(3, o.Select(t => t.Kind).Distinct().Count(), "разные виды");
            }
            s.TrinketSlots[0] = new Trinket(TrinketKind.Ember, 2);
            for (int i = 0; i < 200; i++) Assert.IsTrue(s.RollTrinketOffers(3, 1).Any(t => t.Kind == TrinketKind.Ember), "повышение в выборе");
            s.TrinketSlots[0].Level = TrinketBook.MaxLevel;
            for (int i = 0; i < 200; i++) Assert.IsFalse(s.RollTrinketOffers(3, 1).Any(t => t.Kind == TrinketKind.Ember), "на V не предлагается");
        }

        /// <summary>Комната мини-босса в конце даёт три тринкета уровня I вместо карт; обычная — две карты.</summary>
        [Test]
        public void MiniBossRoomDropsTrinkets()
        {
            foreach (var (room, trinkets) in new[] { ("A_MiniBoss01", true), ("A_Combat01", false) })
            {
                var s = new ArenaSim(3, RoomLibrary.Get(room));
                s.EnableForms();
                s.Player.BuffInvuln = 999f;
                Assert.AreEqual(trinkets, s.IsMiniBossRoom, room);
                for (int i = 0; i < 60 * 300 && !s.Pedestal; i++)
                {
                    s.Step(1f / 60f, default);
                    foreach (var e in s.Enemies) if (e.Alive) s.Damage(e, 99999f, false);
                }
                Assert.IsTrue(s.Pedestal, room);
                if (trinkets)
                {
                    Assert.AreEqual(3, s.Loot.Count, room);
                    Assert.IsTrue(s.Loot.All(g => g.Trinket != null && g.Card == null && g.Trinket.Level == 1), room);
                }
                else
                {
                    Assert.AreEqual(2, s.Loot.Count, room);
                    Assert.IsTrue(s.Loot.All(g => g.Card != null && g.Trinket == null), room);
                }
                foreach (var g in s.Loot) Assert.IsTrue(s.InsideRoom(g.Pos, s.ArenaCfg.pedestalClearMin - 0.15f), $"{room}: место у {g.Pos}");
            }
        }
    }
}
