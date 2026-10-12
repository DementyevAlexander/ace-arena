using Arena.Core;
using Arena.Sim;
using NUnit.Framework;
using UnityEngine;

namespace Arena.Tests
{
    /// <summary>Арены из Hades II (Resources/Arenas): конфиги читаются, Ace не проходит сквозь препятствия, враги — на точках спавна.</summary>
    public class ArenaLayoutTests
    {
        static readonly Vector2[] Dirs =
        {
            new(1, 0), new(-1, 0), new(0, 1), new(0, -1), new(0.7f, 0.7f), new(-0.7f, 0.7f), new(0.7f, -0.7f), new(-0.7f, -0.7f),
        };

        static ArenaSim Sim(ArenaLayout a)
        {
            var s = new ArenaSim(5, layout: a) { WavesEnabled = false };
            s.SetDefaultDeck(staff: false); // телепорт Jump Scare в слоте 3
            return s;
        }

        [Test]
        public void IndexHasThirtyArenasAndActiveOneLoads()
        {
            var index = ArenaCatalog.Index();
            Assert.AreEqual(30, index.arenas.Count);
            CollectionAssert.Contains(index.arenas, index.active);
            var a = ArenaCatalog.LoadActive();
            Assert.IsNotNull(a);
            Assert.AreEqual(index.active, a.id);
        }

        [Test]
        public void EveryArenaLoadsAndStartsFree()
        {
            foreach (var id in ArenaCatalog.Index().arenas)
            {
                var a = ArenaCatalog.Load(id);
                Assert.IsNotNull(a, id);
                Assert.Greater(a.walls.Count, 0, id);
                Assert.GreaterOrEqual(a.spawns.Count, 5, id);
                foreach (var sp in a.spawns)
                    Assert.IsFalse(a.walls.Exists(w => w.Contains(sp)), $"{id}: точка спавна {sp} внутри препятствия");
                var s = Sim(a);
                s.Step(T.Dt, default);
                Assert.IsFalse(s.HitsObstacle(s.Player.Pos, s.PlayerCfg.radius * 0.95f), $"{id}: Ace стоит в препятствии");
            }
        }

        [Test]
        public void RunningNeverEntersWalls()
        {
            foreach (var id in ArenaCatalog.Index().arenas)
            {
                var a = ArenaCatalog.Load(id);
                foreach (var d in Dirs)
                {
                    var s = Sim(a);
                    for (int i = 0; i < 240; i++)
                    {
                        s.Step(T.Dt, new SimInput { Move = d });
                        Assert.IsFalse(s.HitsObstacle(s.Player.Pos, s.PlayerCfg.radius * 0.9f), $"{id}: Ace вошёл в препятствие, бег {d}");
                    }
                }
            }
        }

        [Test]
        public void TeleportStopsBeforeWalls()
        {
            var a = ArenaCatalog.LoadActive();
            foreach (var d in Dirs)
            {
                var s = Sim(a);
                // Jump Scare — слот 3 колоды по умолчанию, телепорт на 4 юнита
                for (int k = 0; k < 4; k++)
                {
                    T.Press(s, 3, d);
                    T.Run(s, 0.6f, new SimInput { Move = d });
                    Assert.IsFalse(s.HitsObstacle(s.Player.Pos, s.PlayerCfg.radius * 0.9f), $"телепорт {d} закончился в препятствии");
                    foreach (var slot in s.Slots) slot.Reset();
                }
            }
        }

        [Test]
        public void EnemiesAppearOnHadesSpawnPoints()
        {
            var a = ArenaCatalog.LoadActive();
            var s = new ArenaSim(9, layout: a);
            T.Run(s, 0.1f); // первая волна стоит в комнате с начала (просыпается через 0.45–0.75 с)
            Assert.Greater(s.Enemies.Count, 0);
            foreach (var e in s.Enemies)
            {
                Assert.IsTrue(a.spawns.Exists(p => (p - e.Pos).magnitude < 0.3f), $"враг не у точки спавна: {e.Pos}");
                Assert.GreaterOrEqual((e.Pos - s.Player.Pos).magnitude, 5f);
            }
        }
    }
}
