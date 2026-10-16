using System.Collections.Generic;
using System.Linq;
using Arena.Core;
using Arena.Sim;
using NUnit.Framework;
using UnityEngine;

namespace Arena.Tests
{
    /// <summary>Хаб: от точки появления пешком доходишь до врат и до каждого персонажа; мишени не умирают; врата дают
    /// событие начала забега; забег начинается с нуля; выход с пятого этажа — забег пройден.</summary>
    public class HubTests
    {
        static ArenaSim NewHub()
        {
            var s = new ArenaSim(5);
            s.EnableForms();
            s.EnterHub();
            return s;
        }

        [Test]
        public void HubIsWalkableToGateAndNpcs()
        {
            var s = NewHub();
            Assert.IsTrue(s.HubMode);
            Assert.AreEqual(HubBuilder.Name, s.LocationTitle);
            Assert.IsTrue(s.InsideRoom(s.Player.Pos, s.PlayerCfg.radius), "старт на полу");
            // поиск в ширину по сетке 0.25 ю с радиусом Смерти: до каждого места — ближе HubReach
            const float step = 0.25f;
            float r = s.PlayerCfg.radius;
            var start = s.Player.Pos;
            var seen = new HashSet<Vector2Int> { Vector2Int.zero };
            var q = new Queue<Vector2Int>();
            q.Enqueue(Vector2Int.zero);
            var reached = new HashSet<HubSpot>();
            while (q.Count > 0 && seen.Count < 200000)
            {
                var c = q.Dequeue();
                var p = start + new Vector2(c.x, c.y) * step;
                foreach (var spot in s.HubSpots) if ((spot.Pos - p).magnitude < s.HubReach * 0.9f) reached.Add(spot);
                foreach (var d in new[] { Vector2Int.up, Vector2Int.down, Vector2Int.left, Vector2Int.right })
                {
                    var n = c + d;
                    if (seen.Contains(n) || !s.InsideRoom(start + new Vector2(n.x, n.y) * step, r)) continue;
                    seen.Add(n);
                    q.Enqueue(n);
                }
            }
            var missed = s.HubSpots.Where(x => !reached.Contains(x)).Select(x => x.Name).ToList();
            Assert.IsEmpty(missed, "не дойти до: " + string.Join(", ", missed));
        }

        [Test]
        public void DummiesStayAndNeverDie()
        {
            var s = NewHub();
            var dummies = s.Enemies.Where(e => e.Dummy).ToList();
            Assert.AreEqual(3, dummies.Count);
            for (int i = 0; i < 120; i++) s.Step(1f / 60f, default);
            foreach (var d in dummies)
            {
                var home = d.Pos;
                s.Damage(d, 1e7f, false);
                for (int i = 0; i < 60; i++) s.Step(1f / 60f, default);
                Assert.IsFalse(d.Dead);
                Assert.Greater(d.Hp, 0f);
                Assert.Less((d.Pos - home).magnitude, 0.01f);
            }
            Assert.IsFalse(s.GameOver, "мишени не бьют");
            Assert.AreEqual(0, s.Kills);
        }

        [Test]
        public void GateStartsFreshRunAndFifthFloorWins()
        {
            var s = NewHub();
            var gate = s.HubSpots.First(x => x.Kind == HubSpotKind.Gate);
            s.Player.Pos = gate.Pos;
            Assert.IsTrue(s.Interact());
            Assert.IsTrue(s.Events.Exists(e => e.Kind == EventKind.HubGate));
            s.Forms[1].Card = new SkillCard(FormKind.Zone, Trait.Fire);
            s.StartRun();
            Assert.IsFalse(s.HubMode);
            Assert.IsTrue(s.FloorMode && s.Floor.Polygonal);
            Assert.IsNull(s.Forms[1].Card, "забег — с нуля");
            Assert.AreEqual(0, s.Enemies.Count(e => e.Dummy), "мишени остались в хабе");
            for (int i = 0; i < ArenaSim.FloorOrder.Length - 1; i++) s.EnterNextFloor();
            Assert.AreEqual(ArenaSim.FloorOrder.Length - 1, s.FloorIndex, "пятый этаж");
            s.EnterNextFloor();
            Assert.IsTrue(s.RunWon, "выход с пятого — забег пройден");
            Assert.IsTrue(s.GameOver);
            s.EnterHub();
            Assert.IsTrue(s.HubMode);
            Assert.IsFalse(s.GameOver);
            Assert.IsFalse(s.RunWon);
        }
    }
}
