using System.Linq;
using Arena.Core;
using Arena.Sim;
using NUnit.Framework;
using UnityEngine;

namespace Arena.Tests
{
    /// <summary>Мегера — босс этажа по данным Hades: фазы 75 / 50 / 25%, рывок, кнут, молнии, подмога, бой в комнате босса.</summary>
    public class MegaeraTests
    {
        static ArenaSim NewForms()
        {
            var s = new ArenaSim(5, RoomDef.Box(30f, 30f)) { WavesEnabled = false };
            s.EnableForms();
            s.RewardPending = false; s.Offers.Clear();
            s.Obstacles.Clear();
            s.Player.Pos = Vector2.zero;
            s.Player.Face = Vector2.up;
            return s;
        }

        static Enemy Mega(ArenaSim s, Vector2 at)
        {
            var e = s.SpawnMegaera(at);
            T.Run(s, s.MegaeraCfg.setupDelay + 0.05f); // стоит до AISetupDelay
            return e;
        }

        [Test]
        public void HasHadesStats()
        {
            var s = NewForms();
            var e = s.SpawnMegaera(new Vector2(0f, 5f));
            Assert.AreEqual(4400f * HadesScale.Hp, e.MaxHp, 1f, "MaxHealth 4400");
            Assert.AreEqual(Hades.U(51f), e.R, 0.001f);
            Assert.IsTrue(e.Armed[(int)MegaWeapon.Lunge] && e.Armed[(int)MegaWeapon.Whirl]);
            Assert.IsFalse(e.Armed[(int)MegaWeapon.Beam] || e.Armed[(int)MegaWeapon.Lightning] || e.Armed[(int)MegaWeapon.Spawns]);
        }

        /// <summary>Удар не проходит ниже 75%: на пороге — 2 с неуязвимости и новое оружие (веер или молнии).</summary>
        [Test]
        public void StageThresholdClampsAndShifts()
        {
            var s = NewForms();
            s.Player.BuffInvuln = 999f;
            var e = Mega(s, new Vector2(0f, 9f));
            s.Damage(e, 1e7f, false);
            Assert.AreEqual(0.75f * e.MaxHp - 1f, e.Hp, 2f, "не ниже порога");
            Assert.AreEqual(1, e.Stage);
            Assert.Greater(e.Invuln, 0f);
            Assert.IsTrue(e.Armed[(int)MegaWeapon.Beam] ^ e.Armed[(int)MegaWeapon.Lightning], "одно из двух");
            float hp = e.Hp;
            s.Damage(e, 1000f, false);
            Assert.AreEqual(hp, e.Hp, 0.01f, "в смене фазы неуязвима");
            T.Run(s, s.MegaeraCfg.stageShift + 0.1f);
            s.Damage(e, 1000f, false);
            Assert.AreEqual(hp - 1000f, e.Hp, 0.01f);
            for (int k = 0; k < 3; k++) { s.Damage(e, 1e7f, false); T.Run(s, s.MegaeraCfg.stageShift + 0.1f); }
            Assert.IsTrue(e.Dead, "четыре фазы — и всё");
            Assert.IsTrue(e.Armed.All(a => a), "к концу у неё всё оружие");
        }

        [Test]
        public void LungeHitsFromRange()
        {
            var s = NewForms();
            var e = Mega(s, new Vector2(0f, 6f)); // дальше 600 — кнут не выбирает, только рывок
            float hp = s.Player.Hp;
            T.Run(s, 3f);
            Assert.AreEqual(MegaWeapon.Lunge, e.Weapon == (MegaWeapon)(-1) ? MegaWeapon.Lunge : e.Weapon);
            Assert.Less(s.Player.Hp, hp, "рывок попал");
        }

        [Test]
        public void WhirlHitsAroundHer()
        {
            var s = NewForms();
            var e = Mega(s, new Vector2(0f, 1.2f));
            e.LungeRun = 2; // рывков подряд хватит — кнут
            float hp = s.Player.Hp;
            T.Run(s, 1.4f);
            Assert.IsTrue(s.Events.Count >= 0);
            Assert.AreEqual(hp - s.MegaeraCfg.whirlDamage, s.Player.Hp, 1f, "удар кнутом");
        }

        [Test]
        public void LightningStrikesAroundDeath()
        {
            var s = NewForms();
            s.Player.BuffInvuln = 999f;
            var e = s.SpawnMegaera(new Vector2(0f, 5f));
            for (int i = 0; i < 5; i++) e.Armed[i] = i == (int)MegaWeapon.Lightning;
            T.Run(s, s.MegaeraCfg.setupDelay + s.MegaeraCfg.boltsWindup + 0.2f);
            var strikes = s.Effects.Where(f => f.Kind == EffectKind.MegaStrike && !f.Done).ToList();
            Assert.That(strikes.Count, Is.InRange(3, 4), "3–4 круга");
            Assert.IsTrue(strikes.All(f => (f.Pos - s.Player.Pos).magnitude <= s.MegaeraCfg.boltsRing + 0.5f), "вокруг героини");
        }

        [Test]
        public void SpawnsCallReinforcements()
        {
            var s = NewForms();
            s.Player.BuffInvuln = 999f;
            var e = s.SpawnMegaera(new Vector2(0f, 5f));
            e.Armed[(int)MegaWeapon.Spawns] = true; // подмога — как только можно (ForceUseIfReady)
            T.Run(s, s.MegaeraCfg.setupDelay + 0.5f);
            Assert.That(s.Enemies.Count(o => o.Owner == e), Is.InRange(5, 6), "«Ко мне!» — подмога");
            Assert.IsTrue(s.Enemies.Where(o => o.Owner == e).All(o => o.Kind is EnemyKind.Thug or EnemyKind.Witch));
        }

        [Test]
        public void CrowdControlIsShorter()
        {
            var s = NewForms();
            var e = Mega(s, new Vector2(0f, 5f));
            s.ApplyCc(e, CcKind.Stun, 1f);
            Assert.AreEqual(s.MegaeraCfg.ccMul, e.Stun, 0.001f);
        }

        /// <summary>Комната босса этажа — Мегера; убита — подмога гибнет, комната пройдена, награда — тринкеты II.</summary>
        [Test]
        public void FloorBossIsMegaeraAndWipesAdds()
        {
            var s = new ArenaSim(5);
            s.EnableForms();
            s.EnterHub();
            s.StartRun();
            s.Player.BuffInvuln = 9999f;
            var room = s.Floor.Rooms[s.Floor.Boss];
            s.Player.Pos = room.Center;
            Enemy mega = null;
            for (int k = 0; k < 300 && mega == null; k++) { s.Step(T.Dt, default); mega = s.Enemies.FirstOrDefault(e => e.Kind == EnemyKind.Megaera); }
            Assert.IsNotNull(mega, "в комнате босса — Мегера");
            T.Run(s, 20f); // фоновая подмога с 15-й секунды, проявляется 2 с
            Assert.IsTrue(s.Enemies.Any(e => !e.Dead && e.Kind != EnemyKind.Megaera), "подмога пришла");
            for (int k = 0; k < 4 && !mega.Dead; k++) { s.Damage(mega, 1e7f, false); T.Run(s, s.MegaeraCfg.stageShift + 0.1f); }
            Assert.IsTrue(mega.Dead);
            Assert.IsFalse(s.Enemies.Any(e => e.Alive), "подмога погибла с ней");
            for (int k = 0; k < 60 && s.Loot.Count == 0; k++) s.Step(T.Dt, default);
            Assert.IsTrue(s.Loot.Count > 0 && s.Loot.All(g => g.Trinket != null && g.Trinket.Level == s.TrinketCfg.bossLevel), "награда — тринкеты II");
        }
    }
}
