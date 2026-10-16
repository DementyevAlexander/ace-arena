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

        static void Hold(ArenaSim s, int slot, float seconds, Vector2 move = default)
        {
            s.Step(T.Dt, new SimInput { Pressed = 1 << slot, Held = 1 << slot, Move = move });
            T.Run(s, seconds, new SimInput { Held = 1 << slot, Move = move });
            s.Step(T.Dt, new SimInput { Released = 1 << slot, Move = move });
        }

        static int Casts(ArenaSim s, float seconds)
        {
            int n = 0;
            for (int i = 0; i < Mathf.RoundToInt(seconds / T.Dt); i++)
            {
                s.Step(T.Dt, default);
                foreach (var ev in s.Events) if (ev.Kind == EventKind.CardCast) n++;
            }
            return n;
        }

        static void Tap(ArenaSim s, int slot) => T.Press(s, slot);

        static Enemy Dummy(ArenaSim s, Vector2 pos, EnemyKind kind = EnemyKind.Wastrel)
        {
            var e = T.Spawn(s, kind, pos);
            e.State = AiState.Recover; e.T = 999f; // стоит и не бьёт
            return e;
        }

        [Test]
        public void StartsWithShotAndDashAndNoCard()
        {
            var s = new ArenaSim(5);
            s.EnableForms();
            Assert.AreEqual(FormKind.Shot, s.Forms[0].Card.Form);
            Assert.AreEqual(FormKind.Dash, s.Forms[ArenaSim.DashSlot].Card.Form);
            Assert.IsNull(s.Forms[1].Card);
            Assert.IsFalse(s.RewardPending, "карточки — только в конце комнаты");
            Assert.AreEqual(0, s.Offers.Count);
            Assert.AreEqual(0, s.Loot.Count);
            Assert.IsFalse(s.Pedestal);
        }

        [Test]
        public void DashCardsGoOnlyToDashSlotAndFusionHasTwoCells()
        {
            var s = new ArenaSim(5);
            s.EnableForms();
            s.RewardPending = true;
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

        /// <summary>Редкость задаёт число вливаний: редкая — 2, эпическая — 3, легендарная — 4. Считаются ячейки карты в слоте,
        /// редкость донора не важна. Стартовые снаряд и рывок — редкие.</summary>
        [Test]
        public void RarityGivesSoulCells()
        {
            var s = new ArenaSim(5);
            s.EnableForms();
            Assert.AreEqual(Rarity.Rare, s.Forms[0].Card.Rarity);
            Assert.AreEqual(Rarity.Rare, s.Forms[ArenaSim.DashSlot].Card.Rarity);
            foreach (var (rarity, cells) in new[] { (Rarity.Rare, 2), (Rarity.Epic, 3), (Rarity.Legendary, 4) })
            {
                s.Forms[1].Card = new SkillCard(FormKind.Zone, Trait.Frost, rarity);
                Assert.AreEqual(cells, s.Forms[1].Card.SoulCells);
                for (int k = 0; k < cells; k++)
                {
                    s.RewardPending = true;
                    s.Offers.Clear();
                    s.Offers.Add(new SkillCard(FormKind.Shot, Trait.Fire, Rarity.Legendary));
                    Assert.IsTrue(s.CanFuse(0, 1), $"{rarity}: вливание {k + 1}");
                    s.FuseOffer(0, 1);
                }
                s.RewardPending = true;
                s.Offers.Clear();
                s.Offers.Add(new SkillCard(FormKind.Shot, Trait.Fire, Rarity.Legendary));
                Assert.IsFalse(s.CanFuse(0, 1), $"{rarity}: ячейки заполнены");
                Assert.AreEqual(1 + cells, s.Forms[1].Card.Level(Trait.Fire) + s.Forms[1].Card.Level(Trait.Frost));
                s.LeaveLoot();
            }
        }

        /// <summary>Редкость выпадает по весам ArenaTuning.rarityWeights (70 / 25 / 5); нулевой вес — не выпадает никогда.</summary>
        [Test]
        public void RarityRollFollowsWeights()
        {
            var rng = new System.Random(11);
            var weights = new ArenaTuning().rarityWeights;
            var n = new int[3];
            const int Total = 20000;
            for (int i = 0; i < Total; i++) n[(int)Rarities.Roll(rng, weights)]++;
            float sum = weights[0] + weights[1] + weights[2];
            for (int r = 0; r < 3; r++)
                Assert.AreEqual(weights[r] / sum, n[r] / (float)Total, 0.015f, ((Rarity)r).ToString());
            for (int i = 0; i < 2000; i++) Assert.AreNotEqual(Rarity.Legendary, Rarities.Roll(rng, new[] { 1f, 1f, 0f }));
        }

        /// <summary>Заряд: усиление (очередь из двух, урон ×2) — только если отпустил в окне 0.6–0.8 с; раньше порога и после
        /// окна — обычный выстрел. Снаряд атакующий — пока заряжаешь, стоишь.</summary>
        [Test]
        public void ShotChargeReleasesVolley()
        {
            var s = NewForms();
            var e = Dummy(s, new Vector2(0f, 3f));
            float hp = e.Hp;
            Hold(s, 0, 0.6f, Vector2.right); // 0.63 с — в окне; стик вправо
            Assert.AreEqual(0f, s.Player.Pos.x, 0.02f, "атакующий заряд — стоит");
            Assert.AreEqual(2, 1 + Casts(s, 0.6f), "в окне: очередь из двух");
            Assert.Less(e.Hp, hp - 30f * HadesScale.Hp * 1.9f, "два попадания");

            var t = NewForms();
            Hold(t, 0, 1.2f); // окно пропущено
            Assert.AreEqual(1, 1 + Casts(t, 0.6f), "окно пропущено — обычный выстрел");

            var u = NewForms();
            Hold(u, 0, 0.05f); // меньше MinChargeToFire — обычный выстрел
            Assert.AreEqual(1, 1 + Casts(u, 0.6f));
        }

        [Test]
        public void ChargedZoneIsWiderAndBarrierHoldsMore()
        {
            var s = NewForms();
            s.Forms[1].Card = new SkillCard(FormKind.Zone);
            Hold(s, 1, 0.6f);
            var z = s.Effects.Find(f => f.Kind == EffectKind.FormZone);
            Assert.AreEqual(s.FormCfg.zoneRadius * Mathf.Sqrt(2f), z.R, 0.01f, "полный заряд — площадь ×2");

            var b = NewForms();
            b.Forms[2].Card = new SkillCard(FormKind.Barrier);
            Hold(b, 2, 0.6f);
            var bar = b.Effects.Find(f => f.Kind == EffectKind.FormBarrier);
            Assert.AreEqual(b.FormCfg.barrierHits + b.FormCfg.barrierChargeHits, bar.Charges, "в окне: 3 + 3");
        }

        /// <summary>Полоса лавы поперёк коробки 30×30: пол слева и справа — земля, между ними — пол типа Lava и зона лавы.</summary>
        static void LavaStrip(ArenaSim s, float x0, float x1)
        {
            RoomDef.Poly Rect(float a, float b, string type) => new() { type = type, h = 0f, p = new[] { new Vector2(a, -30f), new Vector2(b, -30f), new Vector2(b, 30f), new Vector2(a, 30f) } };
            s.Room.floor = new[] { Rect(-30f, x0, "Floor"), Rect(x0, x1, "Lava"), Rect(x1, 30f, "Floor") };
            s.Hazards.Add(new HazardZone { Type = "Lava", Damage = 5f, Poly = Rect(x0, x1, "Lava").p });
        }

        /// <summary>Зона лавы из карты накрывает землю острова — на земле не жжёт, на лаве жжёт.</summary>
        [Test]
        public void LavaZoneOverGroundDoesNotBurn()
        {
            var s = NewForms();
            s.Player.BuffInvuln = 0f;
            s.Hazards.Add(new HazardZone { Type = "Lava", Damage = 5f, Poly = new[] { new Vector2(-3f, -3f), new Vector2(3f, -3f), new Vector2(3f, 3f), new Vector2(-3f, 3f) } });
            float hp = s.Player.Hp;
            T.Run(s, 2f);
            Assert.AreEqual(hp, s.Player.Hp, 0.01f, "пол коробки — земля, зона лавы поверх неё не жжёт");

            var t = NewForms();
            t.Player.BuffInvuln = 0f;
            LavaStrip(t, -1f, 1f);
            hp = t.Player.Hp;
            T.Run(t, 2f);
            Assert.Less(t.Player.Hp, hp, "на настоящей лаве жжёт");
        }

        [Test]
        public void ChargedDashGoesTwiceAsFar()
        {
            var s = NewForms();
            s.Step(T.Dt, new SimInput { Pressed = 1 << ArenaSim.DashSlot, Held = 1 << ArenaSim.DashSlot });
            T.Run(s, 0.6f, new SimInput { Held = 1 << ArenaSim.DashSlot });
            s.Step(T.Dt, new SimInput { Released = 1 << ArenaSim.DashSlot, Move = Vector2.right });
            Assert.IsTrue(s.FormDashing);
            Assert.GreaterOrEqual(s.FormLeap, 0f, "заряженный рывок — прыжок");
            T.Run(s, 0.5f);
            Assert.AreEqual(2f * 320f * HadesScale.Unit, s.Player.Pos.x, 0.1f, "дальность ×2");
        }

        /// <summary>Защитные формы (рывок, барьер) — во время заряда ходишь на полной скорости; атакующие (призыв) — стоишь.
        /// Пропустил окно — обычное применение.</summary>
        [Test]
        public void DefensiveChargeAllowsWalkingAndMissedWindowIsPlain()
        {
            var s = NewForms();
            s.Forms[2].Card = new SkillCard(FormKind.Barrier);
            Hold(s, 2, 1.2f, Vector2.right); // окно пропущено
            Assert.Greater(s.Player.Pos.x, 2f, "барьер заряжается на ходу");
            var bar = s.Effects.Find(f => f.Kind == EffectKind.FormBarrier);
            Assert.AreEqual(s.FormCfg.barrierHits, bar.Charges, "окно пропущено — обычный барьер");

            var m = NewForms();
            m.Forms[1].Card = new SkillCard(FormKind.Summon);
            Hold(m, 1, 0.6f, Vector2.right);
            Assert.AreEqual(0f, m.Player.Pos.x, 0.02f, "призыв атакующий — стоит");

            var d = NewForms();
            d.Step(T.Dt, new SimInput { Pressed = 1 << ArenaSim.DashSlot, Held = 1 << ArenaSim.DashSlot });
            T.Run(d, 1.2f, new SimInput { Held = 1 << ArenaSim.DashSlot });
            float x0 = d.Player.Pos.x;
            d.Step(T.Dt, new SimInput { Released = 1 << ArenaSim.DashSlot, Move = Vector2.right });
            T.Run(d, 0.5f);
            Assert.AreEqual(320f * HadesScale.Unit, d.Player.Pos.x - x0, 0.1f, "окно пропущено — обычная дальность");
        }

        /// <summary>Рывок, который кончился бы в лаве, тянется до берега (не дальше ×4/3) прыжком — без урона.</summary>
        [Test]
        public void DashOverLavaLeapsToShore()
        {
            var s = NewForms();
            s.Player.BuffInvuln = 0f;
            LavaStrip(s, 1.5f, 3f);
            float hp = s.Player.Hp;
            T.Press(s, ArenaSim.DashSlot, Vector2.right);
            Assert.GreaterOrEqual(s.FormLeap, 0f, "над лавой — прыжок");
            T.Run(s, 0.4f);
            Assert.IsFalse(s.OnLava(s.Player.Pos), "приземлился на берегу");
            Assert.Greater(s.Player.Pos.x, 3f);
            Assert.AreEqual(hp, s.Player.Hp, 0.01f, "лава не обожгла");
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
            T.Press(s, 3, Vector2.right); // рывок — при отпускании, по стику
            T.Run(s, 0.3f);
            Assert.IsTrue(e.Dead, "рывок с Пронзанием бьёт задетых");
            Assert.LessOrEqual(s.Forms[ArenaSim.DashSlot].Cd, 0f, "Жатва: добивание рывком сразу восстанавливает рывок");
        }

        [Test]
        public void PlainDashIsInvulnerableAndHasHadesCooldown()
        {
            var s = NewForms();
            s.Player.BuffInvuln = 0f;
            T.Press(s, 3, Vector2.right); // рывок — при отпускании
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
            Hold(s, 0, 0.6f); // в окне — очередь из двух
            T.Run(s, 0.6f);
            Tap(s, 0);         // третье попадание
            T.Run(s, 0.5f);
            Assert.Greater(e.Frozen, 0f, "третье попадание замораживает");
            Assert.Greater(e.RotT, 0f);
            s.Damage(e, 99999f, false);
            Assert.Greater(n.RotT, 0f, "умерший с гнилью заражает соседей");
        }

        /// <summary>Конец комнаты: на пятачке появляются две карточки разных свойств, рядом по горизонтали экрана.
        /// Пока вырастают — не взять; брать можно только вблизи (140); закрыть выбор — обе на месте; применил одну —
        /// вторая сгорела, пятачок пустой; в новой комнате — новый.</summary>
        [Test]
        public void TwoCardsRiseAtRoomEndOneTakenOtherBurns()
        {
            var s = new ArenaSim(7, RoomLibrary.Get("A_Combat01"));
            s.EnableForms();
            s.Player.BuffInvuln = 999f;
            int rewards = 0, rooms = 0;
            bool checkedReach = false;
            for (int i = 0; i < 60 * 120 && rewards < 2; i++)
            {
                s.Step(T.Dt, default);
                foreach (var e in s.Enemies) if (e.Alive) s.Damage(e, 99999f, false);
                Assert.IsFalse(s.RewardPending, "выбор открывается только подбором");
                if (s.Loot.Count == 0) continue;
                Assert.IsNull(s.Enemies.Find(e => e.Alive), "пятачок — когда комната пройдена");
                Assert.IsTrue(s.Pedestal);
                if (s.IsMiniBossRoom) // там тринкеты (TrinketTests) — дальше, ничего не беря
                {
                    for (int k = 0; k < 600 && !s.DoorsOpen; k++) s.Step(T.Dt, default);
                    s.Player.Pos = s.Room.exits[0].pos;
                    s.Step(T.Dt, default);
                    continue;
                }
                Assert.AreEqual(2, s.Loot.Count, "две карточки");
                Assert.AreNotEqual(s.Loot[0].Card.Own, s.Loot[1].Card.Own, "свойства разные");
                var axis = s.Loot[1].Pos - s.Loot[0].Pos;
                Assert.AreEqual(2f * s.ArenaCfg.pedestalHalf, axis.magnitude, 0.01f);
                Assert.Greater(Vector2.Dot(axis.normalized, ArenaSim.ScreenRight), 0.99f, "рядом по горизонтали экрана");
                foreach (var g in s.Loot) Assert.IsTrue(s.InsideRoom(g.Pos, s.ArenaCfg.pedestalClearMin - 0.15f), "вокруг карточки свободно");
                if (!checkedReach)
                {
                    checkedReach = true;
                    s.Player.Pos = s.Loot[0].Pos;
                    Assert.IsFalse(s.PickUpLoot(), "карточки ещё вырастают");
                    for (int k = 0; k < 120 && s.Time < s.LootReadyAt; k++) s.Step(T.Dt, default);
                    s.Player.Pos = s.Loot[0].Pos + Vector2.Perpendicular(ArenaSim.ScreenRight) * (s.ArenaCfg.lootReach + 0.3f);
                    Assert.IsFalse(s.PickUpLoot(), "издалека не подобрать");
                    s.Player.Pos = s.Loot[0].Pos;
                    Assert.IsTrue(s.PickUpLoot());
                    Assert.AreEqual(1, s.Offers.Count);
                    Assert.AreSame(s.Loot[0].Card, s.Offers[0], "открыта ближайшая");
                    s.LeaveLoot();
                    Assert.IsFalse(s.RewardPending);
                    Assert.AreEqual(2, s.Loot.Count, "закрыл выбор — обе на месте");
                }
                for (int k = 0; k < 120 && s.Time < s.LootReadyAt; k++) s.Step(T.Dt, default);
                s.Player.Pos = s.Loot[1].Pos;
                Assert.IsTrue(s.PickUpLoot());
                var card = s.Offers[0];
                Assert.AreSame(s.Loot[1].Card, card);
                s.FuseOffer(0, 0);
                Assert.AreEqual(0, s.Loot.Count, "взята одна — вторая сгорела");
                Assert.IsTrue(s.Events.Exists(e => e.Kind == EventKind.LootBurned && e.Value == 0), "сгорела именно другая");
                Assert.IsTrue(s.Pedestal, "пятачок остаётся пустым");
                Assert.IsTrue(s.Forms[0].Card.Has(card.Own.Value));
                rewards++;
                // в дверь — следующая комната
                for (int k = 0; k < 600 && !s.DoorsOpen; k++) s.Step(T.Dt, default);
                Assert.IsTrue(s.DoorsOpen);
                s.Player.Pos = s.Room.exits[0].pos;
                s.Step(T.Dt, default);
                rooms++;
            }
            Assert.AreEqual(2, rewards);
            Assert.AreEqual(2, s.Rewards);
        }

        /// <summary>Наведение Hades (Aim Assist): снаряд — как рельса, ближайший враг в радиусе 710 во все стороны, и с мышью тоже;
        /// опция выключена — ровно в курсор.</summary>
        [Test]
        public void ShotLocksNearestEnemyEvenWithMouse()
        {
            var s = NewForms();
            var near = Dummy(s, new Vector2(2f, 0f));
            Dummy(s, new Vector2(0f, 4f));
            var aim = new SimInput { UseMouse = true, HasCursor = true, Cursor = new Vector2(0f, 4f) };
            T.Hold(s, 0, 0.1f, aim);
            Assert.AreSame(near, s.Player.Lock, "ближайший, хотя курсор на дальнем");
            Assert.Greater(Vector2.Dot(s.Player.Face, Vector2.right), 0.95f);
            T.Release(s, 0, aim);
            T.Run(s, 0.5f, new SimInput { UseMouse = true, HasCursor = true, Cursor = aim.Cursor }); // перезарядка выстрела

            s.PlayerCfg.aimAssist = false;
            T.Hold(s, 0, 0.1f, aim);
            Assert.IsNull(s.Player.Lock);
            Assert.Greater(Vector2.Dot(s.Player.Face, Vector2.up), 0.95f, "без помощи — в курсор");
        }

        /// <summary>Зона — как каст Hades: с мышью ровно в курсор (AutoLockWithMouse = false), со стиком — на ближайшего в ±75°.</summary>
        [Test]
        public void ZoneGoesToCursorWithMouseAndLocksWithStick()
        {
            var s = NewForms();
            s.Forms[1].Card = new SkillCard(FormKind.Zone);
            var e = Dummy(s, new Vector2(0.5f, 4f));
            s.Step(T.Dt, new SimInput { UseMouse = true, HasCursor = true, Cursor = new Vector2(3f, 0f), Pressed = 2, Held = 2 });
            s.Step(T.Dt, new SimInput { UseMouse = true, HasCursor = true, Cursor = new Vector2(3f, 0f), Released = 2 }); // зона — при отпускании
            var zone = s.Effects.Find(f => f.Kind == EffectKind.FormZone);
            Assert.IsNotNull(zone);
            Assert.Less((zone.Pos - new Vector2(3f, 0f)).magnitude, 0.01f, "мышь — точка курсора, враг не притягивает");

            var p = NewForms();
            p.Forms[1].Card = new SkillCard(FormKind.Zone);
            var target = Dummy(p, new Vector2(1f, 4f));
            Dummy(p, new Vector2(-3f, -1f));
            p.Step(T.Dt, new SimInput { Pressed = 2, Held = 2 });
            p.Step(T.Dt, new SimInput { Move = Vector2.up, Released = 2 });
            var z2 = p.Effects.Find(f => f.Kind == EffectKind.FormZone);
            Assert.IsNotNull(z2);
            Assert.Less((z2.Pos - target.Pos).magnitude, 0.01f, "стик — на врага в угле от стика");
        }

        [Test]
        public void CardLeftOnGroundIsLostInNextRoom()
        {
            var s = new ArenaSim(7, RoomLibrary.Get("A_Combat01"));
            s.EnableForms();
            s.Player.BuffInvuln = 999f;
            for (int i = 0; i < 60 * 120 && !s.DoorsOpen; i++)
            {
                s.Step(T.Dt, default);
                foreach (var e in s.Enemies) if (e.Alive) s.Damage(e, 99999f, false);
            }
            Assert.IsTrue(s.DoorsOpen, "дверь открывается и без взятой карточки");
            Assert.AreEqual(2, s.Loot.Count);
            s.Player.Pos = s.Room.exits[0].pos;
            s.Step(T.Dt, default);
            Assert.AreEqual(0, s.Loot.Count, "не взятые карточки остались в прошлой комнате");
            Assert.IsFalse(s.Pedestal);
            Assert.AreEqual(0, s.Rewards);
        }

        /// <summary>Во всех комнатах пятачок встаёт на пол, куда можно дойти: вокруг каждой карточки нет стен, урн,
        /// ловушек и лавы хотя бы на pedestalClearMin (с поправкой на шаг сетки).</summary>
        [Test]
        public void PedestalFitsInEveryRoom()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            foreach (var name in RoomLibrary.All)
            {
                var s = new ArenaSim(3, RoomLibrary.Get(name));
                s.EnableForms();
                s.Player.BuffInvuln = 999f;
                for (int i = 0; i < 60 * 300 && !s.Pedestal; i++)
                {
                    s.Step(T.Dt, default);
                    foreach (var e in s.Enemies) if (e.Alive) s.Damage(e, 99999f, false);
                }
                Assert.IsTrue(s.Pedestal, name);
                foreach (var g in s.Loot)
                {
                    Assert.IsTrue(s.InsideRoom(g.Pos, s.ArenaCfg.pedestalClearMin - 0.15f), $"{name}: карточка у стены {g.Pos}");
                    foreach (var h in s.Hazards) Assert.IsFalse(Geo.InPoly(h.Poly, g.Pos), $"{name}: карточка в лаве");
                }
            }
            UnityEngine.Debug.Log($"Пятачок во всех комнатах: {watch.ElapsedMilliseconds} мс");
        }
    }
}
