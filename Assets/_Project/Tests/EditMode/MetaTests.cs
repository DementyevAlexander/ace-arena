using System.Linq;
using Arena.Core;
using Arena.Sim;
using NUnit.Framework;
using UnityEngine;

namespace Arena.Tests
{
    /// <summary>Мета-прогресс: стартовая коллекция, Торговец за Сардис, наборы Наставника, Сардис с босса, летопись,
    /// источник в фойе босса, меню персонажей хаба, сохранение в JSON.</summary>
    public class MetaTests
    {
        static ArenaSim NewHub(MetaProgress meta)
        {
            var s = new ArenaSim(5);
            s.EnableForms();
            s.SetMeta(meta);
            s.EnterHub();
            return s;
        }

        static HubSpot Npc(ArenaSim s, HubRole role) => s.HubSpots.Single(x => x.Role == role);

        [Test]
        public void StarterCollectionLimitsDrops()
        {
            var m = MetaProgress.Starter();
            Assert.AreEqual(12, m.CardCount, "3 формы × 4 свойства");
            Assert.IsEmpty(m.trinkets, "тринкеты закрыты");
            var s = NewHub(m);
            for (int i = 0; i < 500; i++)
            {
                var c = s.RollLootCard();
                Assert.Contains(c.Form, MetaProgress.StarterForms);
                Assert.Contains(c.Own.Value, MetaProgress.StarterTraits);
            }
            Assert.IsEmpty(s.RollTrinketOffers(3, 1), "закрытые тринкеты не предлагаются");
        }

        [Test]
        public void MerchantUnlocksForSardis()
        {
            var s = NewHub(MetaProgress.Starter());
            s.Player.Pos = Npc(s, HubRole.Merchant).Pos;
            Assert.IsTrue(s.Interact());
            Assert.AreEqual(HubRole.Merchant, s.Menu);
            var homing = new Unlockable(UnlockKind.Trait, (int)Trait.Homing);
            var summon = new Unlockable(UnlockKind.Form, (int)FormKind.Summon);
            var ember = new Unlockable(UnlockKind.Trinket, (int)TrinketKind.Ember);
            Assert.IsFalse(s.Buy(homing), "нет Сардиса");
            s.AddSardis(3);
            Assert.IsTrue(s.Buy(homing));
            Assert.IsTrue(s.Meta.Has(Trait.Homing));
            Assert.IsTrue(s.Buy(summon));
            Assert.AreEqual(0, s.Meta.sardis, "свойство 1 + форма 2");
            s.AddSardis(4);
            Assert.IsFalse(s.Buy(ember), "тринкет — 5");
            s.AddSardis(1);
            Assert.IsTrue(s.Buy(ember));
            Assert.IsTrue(s.Meta.Dirty, "надо сохранить");
            Assert.IsFalse(s.Buy(homing), "дважды не открыть");
            s.CloseMenu();
            Assert.IsFalse(s.MenuOpen);
        }

        /// <summary>Наставник: три набора по две карты из открытых; взятый — в ПКМ и Q (рывок — в Space) в хабе и в забеге.</summary>
        [Test]
        public void MentorSetGoesIntoRun()
        {
            var s = NewHub(MetaProgress.Starter());
            Assert.AreEqual(3, s.MentorSets.Count);
            foreach (var set in s.MentorSets)
            {
                Assert.AreEqual(2, set.Length);
                Assert.LessOrEqual(set.Count(c => c.Form == FormKind.Dash), 1);
                Assert.IsTrue(set.All(c => s.Meta.Has(c.Form) && s.Meta.Has(c.Own.Value)), "из открытых");
            }
            s.Player.Pos = Npc(s, HubRole.Mentor).Pos;
            Assert.IsTrue(s.Interact());
            Assert.AreEqual(HubRole.Mentor, s.Menu);
            s.ChooseSet(1);
            Assert.IsFalse(s.MenuOpen);
            var chosen = s.MentorSets[1];
            s.StartRun();
            Assert.AreEqual(FormKind.Shot, s.Forms[0].Card.Form);
            Assert.IsNull(s.Forms[0].Card.Own, "в ЛКМ — простой снаряд");
            var slotted = new[] { s.Forms[1].Card, s.Forms[2].Card, s.Forms[ArenaSim.DashSlot].Card }.Where(c => c != null && c.Own.HasValue).ToList();
            foreach (var c in chosen) Assert.IsTrue(slotted.Any(x => x.Form == c.Form && x.Own == c.Own), $"{c.Name} в слотах");
            s.EnterHub();
            Assert.AreEqual(-1, s.ChosenSet, "после забега — новые наборы");
        }

        [Test]
        public void KeeperAndChroniclerOpenMenus()
        {
            var s = NewHub(MetaProgress.Starter());
            s.Player.Pos = Npc(s, HubRole.Keeper).Pos;
            Assert.IsTrue(s.Interact());
            Assert.AreEqual(HubRole.Keeper, s.Menu);
            Assert.IsNull(s.HubNear, "пока меню открыто — с другими не говорить");
            s.CloseMenu();
            s.Player.Pos = Npc(s, HubRole.Chronicler).Pos;
            Assert.IsTrue(s.Interact());
            Assert.AreEqual(HubRole.Chronicler, s.Menu);
        }

        /// <summary>Мегера роняет Сардис, он не теряется; смерть — в летопись.</summary>
        [Test]
        public void BossDropsSardisAndRunGoesToChronicle()
        {
            var s = NewHub(MetaProgress.Starter());
            s.StartRun();
            s.Player.BuffInvuln = 9999f;
            s.Player.Pos = s.Floor.Rooms[s.Floor.Boss].Center;
            Enemy mega = null;
            for (int k = 0; k < 300 && mega == null; k++) { s.Step(T.Dt, default); mega = s.Enemies.FirstOrDefault(e => e.Kind == EnemyKind.Megaera); }
            Assert.IsNotNull(mega);
            for (int k = 0; k < 4 && !mega.Dead; k++) { s.Damage(mega, 1e7f, false); T.Run(s, s.MegaeraCfg.stageShift + 0.1f); }
            Assert.AreEqual(1, s.Meta.megaeraKills);
            var orb = s.Effects.FirstOrDefault(f => f.Kind == EffectKind.Sardis && !f.Done);
            Assert.IsNotNull(orb, "Сардис выпал");
            s.Player.Pos = orb.Pos;
            T.Run(s, 0.6f);
            Assert.AreEqual(1, s.Meta.sardis, "подобран");
            s.Player.BuffInvuln = 0f; s.Player.HitInvuln = 0f;
            s.HurtPlayer(9999f, s.Player.Pos + Vector2.up);
            Assert.IsTrue(s.GameOver);
            Assert.AreEqual(1, s.Meta.runs);
            Assert.AreEqual(1, s.Meta.history.Count);
            Assert.AreEqual(1, s.Meta.bestFloor);
            s.EnterHub();
            Assert.AreEqual(1, s.Meta.sardis, "смерть Сардис не забирает");
        }

        [Test]
        public void FountainInBossFoyerHealsOnce()
        {
            var s = NewHub(MetaProgress.AllUnlocked());
            s.StartRun();
            var f = s.Effects.FirstOrDefault(x => x.Kind == EffectKind.Fountain);
            Assert.IsNotNull(f, "в фойе босса — источник");
            float max = s.PlayerCfg.hp;
            s.Player.Hp = max * 0.4f;
            s.Player.Pos = f.Pos + Vector2.right * 0.6f;
            Assert.IsNotNull(s.FountainNear);
            Assert.IsTrue(s.Interact());
            Assert.AreEqual(max * (0.4f + s.ArenaCfg.fountainHeal), s.Player.Hp, 0.5f);
            Assert.IsNull(s.FountainNear, "иссяк");
            Assert.IsFalse(s.Interact());
        }

        [Test]
        public void MetaSurvivesJson()
        {
            var m = MetaProgress.Starter();
            m.sardis = 7; m.runs = 3; m.megaeraKills = 2;
            m.bonds.Add((int)Bond.Napalm);
            m.history.Add(new RunRecord { floor = 3, kills = 40, rooms = 12, time = 600f });
            var back = MetaProgress.FromJson(m.ToJson());
            Assert.AreEqual(7, back.sardis);
            Assert.AreEqual(3, back.runs);
            Assert.IsTrue(back.Has(Bond.Napalm));
            Assert.IsTrue(back.Has(FormKind.Zone) && !back.Has(FormKind.Summon));
            Assert.AreEqual(40, back.history[0].kills);
            Assert.AreEqual(12, MetaProgress.FromJson("not json").CardCount, "битый файл — новое сохранение");
        }
    }
}
