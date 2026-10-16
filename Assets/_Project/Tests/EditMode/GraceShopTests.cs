using System.Linq;
using Arena.Core;
using Arena.Sim;
using NUnit.Framework;
using UnityEngine;

namespace Arena.Tests
{
    /// <summary>Милость (валюта забега), переброс за Милость, магазин на этаже, скиллы элиток.</summary>
    public class GraceShopTests
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

        static ArenaSim NewRun()
        {
            var s = new ArenaSim(5);
            s.EnableForms();
            s.EnterHub();
            s.StartRun();
            s.Player.BuffInvuln = 999f;
            return s;
        }

        // ---------- Милость ----------

        [Test]
        public void KilledEnemyDropsGraceThatFliesInWhenNear()
        {
            var s = NewForms();
            Assert.AreEqual(0, s.Grace);
            var e = T.Spawn(s, EnemyKind.Wastrel, new Vector2(6f, 0f));
            e.State = AiState.Recover; e.T = 999f;
            s.Damage(e, 99999f, false);
            int orbs = s.Effects.Count(f => f.Kind == EffectKind.Grace && !f.Done);
            Assert.That(orbs, Is.InRange(s.ArenaCfg.graceMin, s.ArenaCfg.graceMax));
            T.Run(s, 1.5f);
            Assert.AreEqual(0, s.Grace, "далеко — лежит");
            var at = s.Effects.First(f => f.Kind == EffectKind.Grace && !f.Done).Pos;
            s.Player.Pos = at + Vector2.left * (s.ArenaCfg.graceMagnet - 0.5f);
            T.Run(s, 1.5f);
            Assert.That(s.Grace, Is.InRange(1, s.ArenaCfg.graceMax), "рядом — притянулась и подобралась");
        }

        [Test]
        public void GraceResetsOnNewRun()
        {
            var s = NewRun();
            s.AddGrace(50);
            s.EnterHub();
            s.StartRun();
            Assert.AreEqual(0, s.Grace);
        }

        /// <summary>Переброс стоит 15 Милости: без неё — нельзя.</summary>
        [Test]
        public void RerollCostsGrace()
        {
            var s = new ArenaSim(7, RoomLibrary.Get("A_Combat01"));
            s.EnableForms();
            s.Player.BuffInvuln = 999f;
            for (int i = 0; i < 60 * 120 && s.Loot.Count == 0; i++)
            {
                s.Step(T.Dt, default);
                foreach (var e in s.Enemies) if (e.Alive) s.Damage(e, 99999f, false);
            }
            for (int k = 0; k < 120 && s.Time < s.LootReadyAt; k++) s.Step(T.Dt, default);
            foreach (var f in s.Effects) if (f.Kind == EffectKind.Grace) f.Done = true; // ромбики с врагов не мешают счёту
            s.Player.Pos = s.Loot[0].Pos;
            s.AddGrace(-s.Grace);
            Assert.IsFalse(s.RerollLoot(), "нет Милости — нет переброса");
            s.AddGrace(20);
            Assert.IsTrue(s.RerollLoot());
            Assert.AreEqual(20 - s.ArenaCfg.rerollCost, s.Grace);
            for (int k = 0; k < 120 && s.Time < s.LootReadyAt; k++) s.Step(T.Dt, default);
            Assert.IsFalse(s.RerollLoot(), "осталось 5 — не хватает");
        }

        // ---------- магазин ----------

        [Test]
        public void EveryFloorHasShop()
        {
            var s = NewRun();
            for (int f = 0; f < ArenaSim.FloorOrder.Length; f++)
            {
                Assert.GreaterOrEqual(s.ShopRoom, 0, $"магазин на этаже {f + 1}");
                Assert.IsFalse(s.Floor.Rooms[s.ShopRoom].Combat, "магазин — мирная комната");
                if (f < ArenaSim.FloorOrder.Length - 1) s.EnterNextFloor();
            }
        }

        /// <summary>Вошёл в магазин — прилавок: 6 карт и 2 тринкета с ценой. Купил одну — Милость списана, остальное на месте;
        /// вышел и вернулся — купленной нет. Без Милости купить нельзя.</summary>
        [Test]
        public void ShopSellsWithoutBurningAndKeepsStock()
        {
            var s = NewRun();
            var room = s.Floor.Rooms[s.ShopRoom];
            s.Player.Pos = room.Center;
            T.Run(s, 0.2f);
            Assert.IsTrue(s.LootIsShop);
            Assert.AreEqual(s.ArenaCfg.shopCards + s.ArenaCfg.shopTrinkets, s.Loot.Count);
            Assert.IsTrue(s.Loot.All(g => g.Price > 0), "у всего цена");
            for (int k = 0; k < 120 && s.Time < s.LootReadyAt; k++) s.Step(T.Dt, default);
            var item = s.Loot.First(g => g.Card != null && g.Card.Form != FormKind.Dash);
            s.Player.Pos = item.Pos;
            Assert.IsTrue(s.PickUpLoot());
            Assert.IsFalse(s.CanTake(0, 1), "нет Милости");
            s.LeaveLoot();
            s.AddGrace(500);
            Assert.IsTrue(s.PickUpLoot());
            Assert.AreSame(item.Card, s.Offers[0]);
            s.TakeOffer(0, 1);
            Assert.AreSame(item.Card, s.Forms[1].Card, "куплено в ПКМ");
            Assert.AreEqual(500 - item.Price, s.Grace);
            Assert.AreEqual(7, s.Loot.Count, "остальное на прилавке");
            Assert.IsFalse(s.Events.Exists(ev => ev.Kind == EventKind.LootBurned), "ничего не сгорело");
            // ушёл в другую комнату и вернулся
            s.Player.Pos = s.Floor.Rooms[s.Floor.Entrance].Center;
            T.Run(s, 0.2f);
            s.Player.Pos = room.Center;
            T.Run(s, 0.2f);
            Assert.IsTrue(s.LootIsShop);
            Assert.AreEqual(7, s.Loot.Count, "купленной нет, остальное ждёт");
        }

        // ---------- элитки ----------

        static Enemy Elite(ArenaSim s, Vector2 pos, params EliteSkill[] skills)
        {
            var e = T.Spawn(s, EnemyKind.Thug, pos);
            e.Elite = true;
            e.MaxHp = e.Hp = 99999f;
            s.SetEliteSkills(e, skills);
            return e;
        }

        [Test]
        public void EliteZoneHurtsDeathStandingInIt()
        {
            var s = NewForms();
            s.Player.BuffInvuln = 0f;
            Elite(s, new Vector2(0f, 6f), EliteSkill.Zone);
            float hp = s.Player.Hp;
            T.Run(s, 0.3f);
            Assert.IsNotNull(s.Enemies[0].Tele, "красный телеграф");
            T.Run(s, 1.5f);
            Assert.IsTrue(s.Effects.Any(f => f.Kind == EffectKind.EliteZone && !f.Done));
            Assert.Less(s.Player.Hp, hp, "круг бьёт");
        }

        [Test]
        public void EliteShieldAbsorbsHitsButNotBurn()
        {
            var s = NewForms();
            var e = Elite(s, new Vector2(0f, 6f), EliteSkill.Barrier);
            T.Run(s, 1.2f);
            Assert.Greater(e.Shield, 0, "щит поднят");
            float hp = e.Hp;
            s.Damage(e, 500f, false);
            Assert.AreEqual(hp, e.Hp, 0.01f, "удар поглощён");
            s.Damage(e, 50f, false, true, DotKind.Burn);
            Assert.AreEqual(hp - 50f, e.Hp, 0.01f, "горение проходит");
        }

        [Test]
        public void EliteDashChargesAlongTelegraph()
        {
            var s = NewForms();
            var e = Elite(s, new Vector2(0f, 7f), EliteSkill.Dash);
            var from = e.Pos;
            T.Run(s, 0.2f);
            Assert.AreEqual(TeleShape.Lane, e.Tele.Shape);
            T.Run(s, 1.2f);
            Assert.IsTrue(s.Events.Count >= 0);
            Assert.Greater((from - e.Pos).magnitude, 3f, "рванула к Смерти");
        }

        [Test]
        public void EliteShotAndSpiritFireBolts()
        {
            var s = NewForms();
            Elite(s, new Vector2(0f, 7f), EliteSkill.Shot, EliteSkill.Summon);
            T.Run(s, 1f);
            Assert.GreaterOrEqual(s.Effects.Count(f => f.Kind == EffectKind.EnemyBolt), s.ArenaCfg.eliteShotCount, "залп");
            T.Run(s, 2.5f);
            Assert.IsTrue(s.Effects.Any(f => f.Kind == EffectKind.EliteSpirit && !f.Done), "дух рядом");
        }

        [Test]
        public void MiniBossElitesGetTwoSkills()
        {
            var s = NewRun();
            for (int r = 0; r < s.Floor.Rooms.Count; r++)
            {
                if (s.Floor.Rooms[r].Elite != 1) continue;
                s.Player.Pos = s.Floor.Rooms[r].Center;
                for (int k = 0; k < 600 && !s.Enemies.Any(e => e.Elite && e.Alive); k++) s.Step(T.Dt, default);
                var elite = s.Enemies.First(e => e.Elite);
                Assert.AreEqual(2, elite.Skills.Length);
                Assert.AreNotEqual(elite.Skills[0], elite.Skills[1]);
                return;
            }
            Assert.Fail("на этаже нет мини-босса");
        }
    }
}
