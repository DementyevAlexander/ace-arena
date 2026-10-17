using System.Collections.Generic;
using System.Linq;
using Arena.Core;
using Arena.Sim;
using NUnit.Framework;
using UnityEngine;

namespace Arena.Tests
{
    /// <summary>Редактор арен: пресеты, независимость слоёв, проверка, выходы, отражение, шаблон генератора, этажи из арен
    /// редактора; снаряды над низкими препятствиями и по дуге над высокими.</summary>
    public class ArenaEditorTests
    {
        static ArenaLayout FromPreset(string name)
        {
            var a = new ArenaLayout("test_" + name);
            ArenaPresets.Apply(a, ArenaPresets.All.First(p => p.Title.Contains(name)));
            return a;
        }

        [Test]
        public void PresetsFitAndPass()
        {
            var ranges = new Dictionary<string, (int lo, int hi)>
            {
                [ArenaPresets.Small] = (150, 220), [ArenaPresets.Medium] = (240, 300), [ArenaPresets.Large] = (380, 480),
            };
            foreach (var p in ArenaPresets.All)
            {
                var b = ArenaPresets.Bounds(p);
                Assert.LessOrEqual(b.width, ArenaLayout.Size, p.Title);
                Assert.LessOrEqual(b.height, ArenaLayout.Size, p.Title);
                int area = ArenaPresets.Area(p);
                var (lo, hi) = ranges[p.Group];
                Assert.That(area, Is.InRange(lo, hi), $"{p.Title}: {area} кл.");
                var a = new ArenaLayout("p");
                ArenaPresets.Apply(a, p);
                Assert.AreEqual(area, a.ShapeCount(), p.Title);
                var rep = ArenaCheck.Check(a);
                Assert.IsTrue(rep.Admitted, $"{p.Title}: " + string.Join("; ", rep.Issues.Where(i => i.Error).Select(i => i.Text)));
                Assert.GreaterOrEqual(rep.WorkingExits, 2, p.Title);
                Assert.IsFalse(rep.Issues.Any(i => i.Text.StartsWith("Узкие")), $"{p.Title}: без узких мест");
            }
        }

        [Test]
        public void LayersStayIndependent()
        {
            var a = FromPreset("Квадрат 17");
            int cx = 16, cy = 16;
            Assert.AreEqual(ArenaSurface.Floor.Code, a.Surf(cx, cy), "новая клетка формы — сразу пол");
            a.SetSurf(cx, cy, ArenaSurface.LavaPool.Code);
            a.objects.Add(new ArenaObject { id = "column", x = cx + 2, y = cy });
            // убрали клетки из формы — поверхность и объект остались в данных, проверка о них сообщает
            a.SetIn(cx, cy, false);
            a.SetIn(cx + 2, cy, false);
            Assert.AreEqual(ArenaSurface.LavaPool.Code, a.Surf(cx, cy));
            Assert.AreEqual(1, a.objects.Count);
            var rep = ArenaCheck.Check(a);
            Assert.IsTrue(rep.Issues.Any(i => !i.Error && i.Text.Contains("за пределами формы") && i.Cells.Contains(new Vector2Int(cx, cy))), "скрытая поверхность — предупреждение");
            Assert.IsTrue(rep.Issues.Any(i => i.Error && i.Text.Contains("Колонна")), "объект за формой — ошибка");
            // вернули клетку — лава на месте
            a.SetIn(cx, cy, true);
            Assert.AreEqual(ArenaSurface.LavaPool.Code, a.Surf(cx, cy));
            // сохранение и чтение
            var b = ArenaLayout.FromJson(a.ToJson());
            Assert.AreEqual(a.ToJson(), b.ToJson());
        }

        [Test]
        public void CheckReportsObjectProblems()
        {
            var a = FromPreset("Квадрат 21");
            a.SetSurf(15, 15, ArenaSurface.LavaPool.Code);
            a.objects.Add(new ArenaObject { id = "bench", x = 15, y = 15 });            // на лаве
            a.objects.Add(new ArenaObject { id = "pavilion", x = 10, y = 10 });         // 10..12
            a.objects.Add(new ArenaObject { id = "spike", x = 11, y = 11 });            // под павильоном
            var rep = ArenaCheck.Check(a);
            Assert.IsFalse(rep.Admitted);
            Assert.IsTrue(rep.Issues.Any(i => i.Error && i.Text.Contains("не на полу")));
            Assert.IsTrue(rep.Issues.Any(i => i.Error && i.Text.Contains("пересекаются")));
            // спавн — только свободный пол
            Assert.IsFalse(rep.Spawns.Contains(new Vector2Int(15, 15)), "не на лаве");
            Assert.IsFalse(rep.Spawns.Contains(new Vector2Int(11, 11)), "не на ловушке и препятствии");
        }

        [Test]
        public void BlockedDoorsAreNotExits()
        {
            var a = FromPreset("Квадрат 13");
            var rep = ArenaCheck.Check(a);
            Assert.AreEqual(8, rep.WorkingExits, "у квадрата 13×13 — по два выхода на сторону (кромка от 12 клеток)");
            // ловушка рядом с каждой дверью — выходы нерабочие, арена не допущена
            foreach (var e in rep.Exits)
            {
                var v = EtgData.DirVec(e.Dir);
                var c = e.Lanes[1] - v;
                a.objects.Add(new ArenaObject { id = "spike", x = c.x, y = c.y });
            }
            rep = ArenaCheck.Check(a);
            Assert.AreEqual(0, rep.WorkingExits);
            Assert.IsFalse(rep.Admitted);
        }

        [Test]
        public void ExitsMustConnectOnFoot()
        {
            var a = FromPreset("Прямоугольник 26");
            // стена из колонн поперёк арены делит её надвое
            var b = ArenaPresets.Bounds(ArenaPresets.All.First(p => p.Title.Contains("Прямоугольник 26")));
            int ox = (a.w - b.width) / 2, oy = (a.h - b.height) / 2;
            for (int y = 0; y < b.height; y++) a.objects.Add(new ArenaObject { id = "column", x = ox + 13, y = oy + y });
            var rep = ArenaCheck.Check(a);
            Assert.IsTrue(rep.Exits.Any(e => !e.Working && e.Why.Contains("пешком")), "выходы другой половины — нерабочие");
        }

        /// <summary>Стены зала встают по внешнему контуру пола и лавы, а не вокруг каждого прямоугольника.</summary>
        [Test]
        public void HallFollowsOuterOutline()
        {
            var a = FromPreset("L 19");
            var o = ArenaCheck.Outlines(a);
            Assert.AreEqual(1, o.Count);
            Assert.AreEqual(6, o[0].Length, "L — шесть углов");
            Assert.AreEqual(261f, Geo.SignedArea(o[0]), 0.01f);
            // лава и пустота внутри: контур тот же внешний
            var b = FromPreset("Квадрат 17");
            b.SetSurf(15, 15, ArenaSurface.LavaPool.Code);
            b.SetIn(12, 12, false);
            o = ArenaCheck.Outlines(b);
            Assert.AreEqual(1, o.Count);
            Assert.AreEqual(4, o[0].Length);
            var t = ArenaCheck.ToTemplate(b, "Editor/x");
            Assert.AreEqual(1, t.Outlines.Length);
        }

        [Test]
        public void TransposeIsMirror()
        {
            var a = FromPreset("L 24");
            a.objects.Add(new ArenaObject { id = "wall", x = 6, y = 8, rot = 0 });
            a.objects.Add(new ArenaObject { id = "phalanx", x = 9, y = 9, rot = 1 });
            var t = a.Transposed();
            Assert.AreEqual(new RectInt(8, 6, 1, 2), ArenaLayout.Footprint(t.objects[0]), "стенка 2×1 → 1×2");
            Assert.AreEqual(0, t.objects[1].rot, "направление ↑ → →");
            Assert.AreEqual(a.ToJson(), a.Transposed().Transposed().ToJson());
        }

        [Test]
        public void TemplateMatchesArena()
        {
            var a = Sample();
            foreach (var t in new[] { ArenaCheck.ToTemplate(a, "Editor/t"), ArenaCheck.ToTemplate(a.Transposed(), "Editor/t/m") })
            {
                Assert.IsNotNull(t);
                Assert.GreaterOrEqual(t.Grid.exits.Length, 2);
                foreach (var e in t.Grid.exits)
                {
                    var v = EtgData.DirVec(e.d);
                    foreach (var c in e.cells)
                    {
                        Assert.AreEqual('.', t.Grid.At(c.x, c.y), $"{t.Name}: клетка выхода на полу");
                        for (var p = new Vector2Int(c.x, c.y) + v; p.x >= 0 && p.y >= 0 && p.x < t.Grid.w && p.y < t.Grid.h; p += v)
                            Assert.AreEqual(' ', t.Grid.At(p.x, p.y), $"{t.Name}: путь наружу свободен");
                    }
                }
                var d = t.Def;
                Assert.IsTrue(d.hazards.Length > 0 && d.hazards.All(h => h.type == "Lava"), "лава — опасный пол");
                Assert.IsTrue(d.walls.Any(w => w.type == PolisDressing.Bench && !w.shots), "лавка пропускает снаряды");
                Assert.IsTrue(d.walls.Any(w => w.type == PolisDressing.Column && w.shots), "колонна держит снаряды");
                Assert.IsTrue(d.traps.Any(x => x.type == "SpikeTrap" && x.param.damage > 0f), "ловушка с параметрами Hades");
            }
        }

        /// <summary>Средняя L-арена: лава, лавки, колонны, павильон, шипы и фаланга.</summary>
        static ArenaLayout Sample()
        {
            var a = FromPreset("L 19");
            var b = ArenaPresets.Bounds(ArenaPresets.All.First(p => p.Title.Contains("L 19")));
            int ox = (a.w - b.width) / 2, oy = (a.h - b.height) / 2;
            for (int y = 2; y < 5; y++) for (int x = 10; x < 14; x++) a.SetSurf(ox + x, oy + y, ArenaSurface.LavaPool.Code);
            a.objects.Add(new ArenaObject { id = "bench", x = ox + 3, y = oy + 3 });
            a.objects.Add(new ArenaObject { id = "column", x = ox + 3, y = oy + 12 });
            a.objects.Add(new ArenaObject { id = "column", x = ox + 6, y = oy + 12 });
            a.objects.Add(new ArenaObject { id = "pavilion", x = ox + 3, y = oy + 6 });
            a.objects.Add(new ArenaObject { id = "spike", x = ox + 8, y = oy + 4 });
            a.objects.Add(new ArenaObject { id = "phalanx", x = ox + 15, y = oy + 6, rot = 1 });
            return a;
        }

        /// <summary>Арены из Resources/Arenas, включённые в генерацию, проходят проверку и без предупреждений.</summary>
        [Test]
        public void IndexedArenasAreAdmitted()
        {
            var index = ArenaLibrary.LoadIndex();
            Assert.Greater(index.enabled.Length, 0);
            foreach (var name in index.enabled)
            {
                var a = ArenaLibrary.Get(name);
                Assert.IsNotNull(a, name);
                foreach (var l in new[] { a, a.Transposed() })
                {
                    var rep = ArenaCheck.Check(l);
                    Assert.IsTrue(rep.Admitted, name + ": " + string.Join("; ", rep.Issues.Where(i => i.Error).Select(i => i.Text)));
                    if (!name.StartsWith(ArenaImport.Prefix)) Assert.IsEmpty(rep.Issues.Select(i => i.Text), name + ": примеры — без предупреждений");
                }
            }
            Assert.AreEqual(index.enabled.Length * 2, ArenaLibrary.Templates(index).Count, "каждая арена и её отражение");
        }

        /// <summary>Арены Тартара переносятся в редактор: одна область, примерно та же площадь пола, препятствия и шипы на
        /// месте, проверка проходит.</summary>
        [Test]
        public void TartarusArenasImport()
        {
            var problems = new List<string>();
            foreach (var room in RoomLibrary.All)
            {
                var d = RoomLibrary.Get(room);
                if (d == null || d.biome != ArenaSim.ArenaBiome) continue;
                var a = ArenaImport.FromRoom(d, ArenaImport.Prefix + room);
                var rep = ArenaCheck.Check(a);
                var info = $"{room}: {a.ShapeCount()} кл., объектов {a.objects.Count} (" +
                           string.Join(", ", a.objects.GroupBy(o => o.id).Select(g => $"{g.Key} {g.Count()}")) + $"), выходов {rep.WorkingExits}/{rep.Exits.Count}";
                TestContext.WriteLine(info);
                foreach (var i in rep.Issues) TestContext.WriteLine((i.Error ? "  ОШИБКА " : "  ") + i.Text);
                if (!rep.Admitted) problems.Add(info + ": " + string.Join("; ", rep.Issues.Where(i => i.Error).Select(i => i.Text)));
                Assert.Greater(a.objects.Count(o => ArenaPieces.Get(o.id).Layer >= ArenaLayout.Low), 0, room + ": препятствия");
                Assert.AreEqual(d.traps.Count(t => t.type == "SpikeTrap"), a.objects.Count(o => o.id == "spike"), room + ": шипы");
            }
            Assert.IsEmpty(problems, string.Join("\n", problems));
        }

        [Test]
        public void FloorsBuildFromEditorArenas()
        {
            var a = Sample();
            var list = new List<ArenaTemplate>();
            foreach (var p in ArenaPresets.All.Where(p => p.Group != ArenaPresets.Small))
            {
                var l = new ArenaLayout("x");
                ArenaPresets.Apply(l, p);
                list.Add(ArenaCheck.ToTemplate(l, ArenaLibrary.Prefix + p.Title));
                list.Add(ArenaCheck.ToTemplate(l.Transposed(), ArenaLibrary.Prefix + p.Title + "/m"));
            }
            list.Add(ArenaCheck.ToTemplate(a, ArenaLibrary.Prefix + "sample"));
            list.Add(ArenaCheck.ToTemplate(a.Transposed(), ArenaLibrary.Prefix + "sample/m"));
            Assert.IsTrue(list.All(t => t != null));
            ArenaSim.UseArenas(new ArenaSet(list));
            try
            {
                for (int seed = 0; seed < 3; seed++)
                {
                    var s = new ArenaSim(seed);
                    s.EnableForms();
                    s.StartFloors(seed, arenas: true);
                    for (int floor = 0; floor < ArenaSim.FloorOrder.Length; floor++)
                    {
                        var f = s.Floor;
                        string tag = $"{f.FloorId} #{seed} {f.Flow}";
                        Assert.IsTrue(f.Rooms.Where(r => r.Arena != null).All(r => r.Arena.Layout != null), $"{tag}: только арены редактора");
                        Assert.Greater(f.Rooms.Count(r => r.Arena != null), 3, tag);
                        foreach (var r in f.Rooms.Where(r => r.Arena != null))
                        {
                            Assert.Greater(r.ArenaSpawns.Length, 10, $"{tag}: {r.Arena.Name} — точки появления");
                            foreach (var sp in r.ArenaSpawns) Assert.IsFalse(s.OnLava(sp.pos), $"{tag}: появление не в лаве");
                        }
                        if (f.Rooms.Any(r => r.Arena != null && r.Arena.Name.StartsWith(ArenaLibrary.Prefix + "sample")))
                            Assert.IsTrue(s.Room.hazards.Any(h => h.type == "Lava"), $"{tag}: лава арены на этаже");
                        ArenaFloorTests.AssertWalkable(s, tag);
                        if (floor + 1 < ArenaSim.FloorOrder.Length) s.EnterNextFloor();
                    }
                }
            }
            finally { ArenaSim.UseArenas(null); }
        }

        // ---------- снаряды ----------

        static ArenaSim ShotSim()
        {
            var s = new ArenaSim(3, RoomDef.Box(30f, 30f)) { WavesEnabled = false };
            s.Obstacles.Clear();
            s.Player.Pos = new Vector2(0f, 10f);
            Vector2[] R(float x0, float x1) => new[] { new Vector2(x0, -2f), new Vector2(x1, -2f), new Vector2(x1, 2f), new Vector2(x0, 2f) };
            s.Obstacles.Add(new Obstacle { Pos = new Vector2(2.5f, 0f), R = 2.1f, Poly = R(2f, 3f), Height = 3f, Shots = true, Type = PolisDressing.Column });
            s.Obstacles.Add(new Obstacle { Pos = new Vector2(-2.5f, 0f), R = 2.1f, Poly = R(-3f, -2f), Height = 0.55f, Shots = false, Type = PolisDressing.Bench });
            return s;
        }

        static Bolt Fire(ArenaSim s, Vector2 dir, bool lob)
        {
            var b = new Bolt { Pos = Vector2.zero, Dir = dir, Speed = 10f, Range = 8f, R = 0.2f, Damage = 1f, Lob = lob };
            s.Bolts.Add(b);
            for (int i = 0; i < 30; i++) s.Step(1f / 60f, default);
            return b;
        }

        [Test]
        public void ShotsFlyOverLowObstacles()
        {
            var s = ShotSim();
            var b = Fire(s, Vector2.left, false);
            Assert.IsFalse(b.Done, "над лавкой снаряд летит дальше");
            Assert.Less(b.Pos.x, -3.5f);
        }

        [Test]
        public void HighObstaclesStopStraightShotsButNotLobs()
        {
            var s = ShotSim();
            var straight = Fire(s, Vector2.right, false);
            Assert.IsTrue(straight.Done, "прямой снаряд упирается в колонну");
            Assert.Less(straight.Pos.x, 2.5f);
            var lob = Fire(ShotSim(), Vector2.right, true);
            Assert.IsFalse(lob.Done, "снаряд по дуге перелетает колонну");
            Assert.Greater(lob.Pos.x, 3.5f);
        }
    }
}
