using System.Collections.Generic;
using Arena.Core;
using Arena.Sim;
using NUnit.Framework;
using UnityEngine;

namespace Arena.Tests
{
    public class ThugTests
    {
        static (ArenaSim, Enemy) Setup(float distance = 1.4f)
        {
            var s = T.NewSim();
            var e = T.Spawn(s, EnemyKind.Thug, new Vector2(0f, distance));
            e.Aggro = true;
            return (s, e);
        }

        [Test]
        public void WindupFlashesWhiteThenTrapezoidHits()
        {
            var (s, e) = Setup();
            s.Step(T.Dt, default);
            Assert.AreEqual(AiState.Windup, e.State, "в радиусе AIAttackDistance сразу замах");
            Assert.AreEqual(0.4f, e.WarnTotal, 1e-4f, "вспышка — последние min(0.5, 0.4) с");
            T.Run(s, 0.3f);
            Assert.Greater(e.WarnT, 0.5f, "к концу замаха враг белеет");
            T.Run(s, 0.4f);
            Assert.AreEqual(s.PlayerCfg.hp - s.ThugCfg.damage, s.Player.Hp, 0.01f, "10 урона Hades в масштабе урона врагов");
            Assert.Greater(s.Player.Stun, 0f, "ZagreusOnHitStun 0.2 с");
            Assert.Less(s.Player.Pos.y, -0.1f, "отбрасывание назад от Thug");
        }

        [Test]
        public void DirectionIsLockedSoSidestepDodges()
        {
            var (s, e) = Setup();
            s.Step(T.Dt, default);
            T.Run(s, 0.53f, new SimInput { Move = Vector2.right });
            T.Run(s, 0.3f);
            Assert.AreEqual(s.PlayerCfg.hp, s.Player.Hp, 0.01f, "AITrackTargetDuringCharge = false: удар туда, куда смотрел в начале");
        }

        [Test]
        public void StunDuringWindupRestartsIt()
        {
            var (s, e) = Setup();
            s.Step(T.Dt, default);
            T.Run(s, 0.2f);
            s.ApplyCc(e, CcKind.Stun, 0.5f);
            Assert.AreEqual(AiState.Chase, e.State);
            Assert.AreEqual(0f, e.WarnT);
            T.Run(s, 0.45f);
            Assert.AreEqual(s.PlayerCfg.hp, s.Player.Hp, 0.01f);
        }

        [Test]
        public void LongRecoveryAfterSwing()
        {
            var (s, e) = Setup();
            s.Step(T.Dt, default);
            T.Run(s, 0.6f);
            Assert.AreEqual(AiState.Strike, e.State);
            T.Run(s, 1f);
            Assert.AreEqual(AiState.Recover, e.State, "PostAttackDuration 1.5 с");
        }
    }

    public class WitchTests
    {
        [Test]
        public void ShootsBoltWhenInSight()
        {
            var s = T.NewSim();
            var w = T.Spawn(s, EnemyKind.Witch, new Vector2(0f, 6f));
            w.Aggro = true; w.State = AiState.Peek; w.T = 3f;
            s.Step(T.Dt, default);
            Assert.AreEqual(AiState.Windup, w.State);
            T.Run(s, 0.55f);
            Assert.AreEqual(1, s.Bolts.Count, "сгусток после замаха 0.5 с");
            T.Run(s, 1.6f);
            Assert.AreEqual(s.PlayerCfg.hp - s.WitchCfg.damage, s.Player.Hp, 0.01f);
        }

        [Test]
        public void HidesBehindCover()
        {
            var s = T.NewSim();
            s.Obstacles.Add(new Obstacle { Pos = new Vector2(0f, 4f), R = 0.8f });
            var w = T.Spawn(s, EnemyKind.Witch, new Vector2(2f, 5f));
            w.Aggro = true; w.State = AiState.Cover; w.T = 0f;
            T.Run(s, 2.5f);
            Assert.Greater(w.Pos.y, 4.8f, "встаёт за камнем со стороны от Ace");
            Assert.IsFalse(s.LineOfSight(w.Pos, s.Player.Pos), "из укрытия Ace не видно");
        }
    }

    /// <summary>Зоны атак врагов Hades: у Thug — трапеция удара вместе с выпадом, у ведьмы — линия выстрела; исчезают с ударом.</summary>
    public class HadesTelegraphTests
    {
        [Test]
        public void ThugWindupShowsTrapezoidUntilStrike()
        {
            var s = T.NewSim();
            s.Player.BuffInvuln = 999f;
            var e = T.Spawn(s, EnemyKind.Thug, new Vector2(0f, 1.6f));
            e.Aggro = true;
            for (int i = 0; i < 120 && e.State != AiState.Windup; i++) s.Step(T.Dt, default);
            Assert.AreEqual(AiState.Windup, e.State);
            Assert.IsNotNull(e.Tele);
            Assert.AreEqual(TeleShape.Trapezoid, e.Tele.Shape);
            Assert.IsTrue(e.Tele.Contains(s.Player.Pos, 0f), "герой в зоне удара");
            for (int i = 0; i < 120 && e.State != AiState.Strike; i++)
            {
                Assert.IsNotNull(e.Tele, "зона держится весь замах");
                s.Step(T.Dt, default);
            }
            Assert.AreEqual(AiState.Strike, e.State);
            Assert.IsNull(e.Tele, "удар — зона исчезла");
        }

        [Test]
        public void WitchWindupShowsLaneToHero()
        {
            var s = T.NewSim();
            s.Player.BuffInvuln = 999f;
            var e = T.Spawn(s, EnemyKind.Witch, new Vector2(0f, 4f), AiState.Peek);
            e.Aggro = true; e.T = 3f;
            for (int i = 0; i < 120 && e.State != AiState.Windup; i++) s.Step(T.Dt, default);
            Assert.AreEqual(AiState.Windup, e.State);
            Assert.IsNotNull(e.Tele);
            Assert.AreEqual(TeleShape.Lane, e.Tele.Shape);
            Assert.IsTrue(e.Tele.Contains(s.Player.Pos, 0.1f), "линия выстрела доходит до героя");
            for (int i = 0; i < 120 && e.State == AiState.Windup; i++) s.Step(T.Dt, default);
            Assert.IsNull(e.Tele, "выстрел — зона исчезла");
        }
    }

    public class RoomTests
    {
        [Test]
        public void ThirtyHadesRoomsAllEnabledStartInTartarus()
        {
            Assert.AreEqual(30, RoomLibrary.All.Count, "30 комнат Hades, арен Hades II больше нет");
            Assert.AreEqual(30, RoomLibrary.Enabled.Count, "подключены все");
            Assert.AreEqual("A_Combat01", RoomLibrary.Enabled[0], "старт — первая комната Тартара");
            Assert.AreEqual("A_Combat01", RoomLibrary.Default().name);
        }

        /// <summary>У каждой комнаты свой набор врагов: Thug, Witch, Wastrel, Casket в разных комбинациях — все четыре встречаются,
        /// наборов много разных.</summary>
        [Test]
        public void RoomsHaveDifferentEnemyCombinations()
        {
            var kinds = new HashSet<string>();
            var combos = new HashSet<string>();
            foreach (var name in RoomLibrary.All)
            {
                var enc = RoomLibrary.Get(name).encounter;
                Assert.IsNotNull(enc, name);
                Assert.Greater(enc.pool.Length, 1, name + ": в наборе хотя бы два вида");
                Assert.AreEqual(enc.pool.Length, enc.ratings.Length, name);
                foreach (var k in enc.pool) { Assert.Contains(k, new[] { "Thug", "Witch", "Wastrel", "Casket" }, name); kinds.Add(k); }
                var sorted = new List<string>(enc.pool);
                sorted.Sort();
                combos.Add(string.Join("+", sorted));
            }
            Assert.AreEqual(4, kinds.Count, "все четыре врага встречаются");
            Assert.GreaterOrEqual(combos.Count, 10, "много разных комбинаций");
        }

        /// <summary>Комната с Wastrel и Casket генерирует только их, и они выходят волной сразу в бой.</summary>
        [Test]
        public void RoomPoolDecidesWhoSpawns()
        {
            var s = new ArenaSim(4, RoomLibrary.Get("A_Combat04"));
            s.Player.BuffInvuln = 999f;
            for (int i = 0; i < 60 * 8 && s.Enemies.Count < 3; i++) s.Step(T.Dt, default);
            Assert.Greater(s.Enemies.Count, 0);
            foreach (var e in s.Enemies) Assert.IsTrue(e.Kind is EnemyKind.Wastrel or EnemyKind.Casket, e.Kind.ToString());
            T.Run(s, 2.5f);
            Assert.IsTrue(s.Enemies.Exists(e => e.Alive && e.Aggro), "враги Hades II выходят агрессивными");
        }

        [Test]
        public void EveryRoomLoadsWithHeroAndSpawnsOnFloor()
        {
            foreach (var name in RoomLibrary.All)
            {
                var room = RoomLibrary.Get(name);
                Assert.IsNotNull(room, name);
                Assert.Greater(room.floor.Length, 0, name);
                var s = new ArenaSim(1, room) { WavesEnabled = false };
                Assert.IsTrue(s.InsideRoom(s.Player.Pos, 0f), name + ": герой стоит на полу");
                int ok = 0;
                foreach (var sp in room.spawns) if (s.InsideRoom(sp.pos, 0f)) ok++;
                Assert.Greater(ok, room.spawns.Length / 2, name + ": точки появления на полу");
            }
        }

        [Test]
        public void WallsStopThePlayer()
        {
            var s = new ArenaSim(1, RoomLibrary.Get("A_Combat01")) { WavesEnabled = false };
            for (int i = 0; i < 4; i++)
            {
                var dir = new Vector2(Mathf.Cos(i * Mathf.PI / 2f + 0.3f), Mathf.Sin(i * Mathf.PI / 2f + 0.3f));
                s.Player.Pos = s.Room.heroStart;
                T.Run(s, 8f, new SimInput { Move = dir });
                Assert.IsTrue(s.InsideRoom(s.Player.Pos, 0f) || NearObstacle(s), "после 8 с бега в стену Ace всё ещё в комнате");
            }
        }

        static bool NearObstacle(ArenaSim s)
        {
            foreach (var o in s.Obstacles) if ((o.Pos - s.Player.Pos).magnitude < o.R + 1f) return true;
            return false;
        }

        [Test]
        public void FirstRoomRunsHadesIntroFight()
        {
            var s = new ArenaSim(1, RoomLibrary.Get("A_Combat01"));
            Assert.AreEqual(1, s.Wave);
            Assert.AreEqual(1, s.Enemies.Count, "волна 1: один Thug уже стоит в комнате");
            Assert.AreEqual(EnemyKind.Thug, s.Enemies[0].Kind);
            Assert.IsTrue(s.Enemies[0].PreSpawned);
            Assert.GreaterOrEqual((s.Enemies[0].Pos - s.Player.Pos).magnitude, Hades.U(750f) - 0.01f);
            Assert.AreEqual(1, s.Traps.Count);
            Assert.AreEqual(TrapKind.Spike, s.Traps[0].Kind);
        }

        [Test]
        public void SpikeTrapHitsWhoeverStandsOnIt()
        {
            var s = new ArenaSim(1, RoomLibrary.Get("A_Combat01")) { WavesEnabled = false };
            s.Enemies.Clear();
            var trap = s.Traps[0];
            s.Player.Pos = trap.Pos;
            s.Step(T.Dt, default);
            Assert.AreEqual(TrapPhase.Pre, trap.Phase, "шаг на площадку");
            T.Run(s, 0.25f);
            Assert.AreEqual(s.PlayerCfg.hp - 5f * Hades.EnemyDamageScale, s.Player.Hp, 0.01f, "5 урона Hades через 0.2 с");
        }
    }
}

namespace Arena.Tests
{
    static class R
    {
        /// <summary>Комната без отражения и без волн.</summary>
        public static ArenaSim Room(string name)
        {
            var room = RoomLibrary.Get(name);
            var s = new ArenaSim(1, room) { WavesEnabled = false, MirrorRooms = false };
            s.LoadRoom(room);
            s.Enemies.Clear();
            return s;
        }

        public static Trap First(ArenaSim s, TrapKind kind) => s.Traps.Find(t => t.Kind == kind);

        /// <summary>Сетка проходимости от точки старта: какие точки достижимы для круга радиуса героя.</summary>
        public static System.Func<Vector2, bool> Reach(ArenaSim s)
        {
            const float step = 0.3f;
            var min = s.Room.min;
            int w = Mathf.CeilToInt((s.Room.max.x - min.x) / step) + 1, h = Mathf.CeilToInt((s.Room.max.y - min.y) / step) + 1;
            var seen = new bool[w, h];
            var q = new System.Collections.Generic.Queue<(int, int)>();
            int sx = Mathf.RoundToInt((s.Room.heroStart.x - min.x) / step), sy = Mathf.RoundToInt((s.Room.heroStart.y - min.y) / step);
            seen[sx, sy] = true; q.Enqueue((sx, sy));
            float r = s.PlayerCfg.radius * 0.8f;
            while (q.Count > 0)
            {
                var (x, y) = q.Dequeue();
                foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h || seen[nx, ny]) continue;
                    if (!s.InsideRoom(min + new Vector2(nx, ny) * step, r)) continue;
                    seen[nx, ny] = true; q.Enqueue((nx, ny));
                }
            }
            return p =>
            {
                int cx = Mathf.RoundToInt((p.x - min.x) / step), cy = Mathf.RoundToInt((p.y - min.y) / step);
                for (int dx = -2; dx <= 2; dx++)
                for (int dy = -2; dy <= 2; dy++)
                {
                    int x = cx + dx, y = cy + dy;
                    if (x >= 0 && y >= 0 && x < w && y < h && seen[x, y]) return true;
                }
                return false;
            };
        }
    }

    public class RoomPlayTests
    {
        [Test]
        public void EveryRoomIsConnectedFromStart()
        {
            foreach (var name in RoomLibrary.All)
            {
                var s = R.Room(name);
                var reach = R.Reach(s);
                int ok = 0;
                foreach (var sp in s.Room.spawns) if (reach(sp.pos)) ok++;
                Assert.GreaterOrEqual(ok, s.Room.spawns.Length * 0.8f, $"{name}: {ok} из {s.Room.spawns.Length} точек появления достижимы");
                bool door = false;
                foreach (var e in s.Room.exits)
                {
                    float reachR = s.DoorReach(e.pos);
                    for (float a = 0f; a < 360f && !door; a += 15f)
                    for (float d = 0f; d <= reachR && !door; d += 0.25f)
                    {
                        var p = e.pos + new Vector2(Mathf.Cos(a * Mathf.Deg2Rad), Mathf.Sin(a * Mathf.Deg2Rad)) * d;
                        if (reach(p)) door = true;
                    }
                }
                Assert.IsTrue(door || s.Room.exits.Length == 0, $"{name}: до двери можно дойти");
            }
        }

        [Test]
        public void LavaIsWalkableAndBurns()
        {
            var s = R.Room("B_Combat09");
            var lava = s.Hazards.Find(h => h.Type == "Lava");
            Assert.IsNotNull(lava);
            var c = Geo.Centroid(lava.Poly);
            Assert.IsTrue(s.InsideRoom(c, 0f), "по магме можно ходить, как в Hades");
            s.Player.Pos = c;
            T.Run(s, 1.1f);
            Assert.Less(s.Player.Hp, s.PlayerCfg.hp, "магма жжёт");
        }

        [Test]
        public void MirroredRoomKeepsGeometryConsistent()
        {
            var room = RoomLibrary.Get("A_Combat01");
            var m = room.Mirrored(ArenaSim.ScreenRight);
            Assert.AreEqual(room.spawns.Length, m.spawns.Length);
            var d = room.spawns[0].pos - m.spawns[0].pos;
            Assert.AreEqual(0f, Vector2.Dot(d, new Vector2(-ArenaSim.ScreenRight.y, ArenaSim.ScreenRight.x)), 1e-3f, "отражение только по горизонтали экрана");
            var s = new ArenaSim(1, m) { WavesEnabled = false, MirrorRooms = false };
            Assert.IsTrue(s.InsideRoom(s.Player.Pos, 0f));
        }

        [Test]
        public void ClearingTheRoomOpensDoorsToTheNextRoom()
        {
            var s = new ArenaSim(1, RoomLibrary.Get("A_Combat01")) { MirrorRooms = false };
            for (int i = 0; i < 4000 && !s.DoorsOpen; i++)
            {
                foreach (var e in s.Enemies) if (e.Alive) s.Damage(e, 99999f, false);
                s.Player.BuffInvuln = 1f;
                s.Step(T.Dt, default);
            }
            Assert.IsTrue(s.DoorsOpen, "четыре волны первого боя пройдены — двери открыты");
            Assert.AreEqual(4, s.Wave);
            s.Player.Pos = s.Room.exits[0].pos;
            s.Step(T.Dt, default);
            Assert.AreEqual(1, s.RoomsCleared);
            Assert.AreEqual(2, s.Depth, "следующая комната глубже");
            Assert.IsFalse(s.DoorsOpen);
            Assert.AreEqual(s.PlayerCfg.hp, s.Player.Hp, 0.01f, "здоровье переносится");
            T.Run(s, 2f);
            Assert.Greater(s.Enemies.Count, 0, "в новой комнате — сгенерированные волны");
        }

        [Test]
        public void ArenaClearedBannerThenDoorLightsUp()
        {
            var s = new ArenaSim(1, RoomLibrary.Get("A_Combat01")) { MirrorRooms = false };
            bool banner = false;
            for (int i = 0; i < 4000 && !banner; i++)
            {
                foreach (var e in s.Enemies) if (e.Alive) s.Damage(e, 99999f, false);
                s.Player.BuffInvuln = 1f;
                s.Step(T.Dt, default);
                foreach (var ev in s.Events) if (ev.Kind == EventKind.ArenaCleared) banner = true;
            }
            Assert.IsTrue(banner, "после последнего врага — надпись «Арена пройдена»");
            Assert.IsFalse(s.DoorsOpen, "пока надпись на экране, дверь не горит");
            s.Player.Pos = s.Room.exits[0].pos;
            T.Run(s, s.ArenaCfg.doorsDelay - 0.2f);
            Assert.IsFalse(s.DoorsOpen);
            Assert.AreEqual(0, s.RoomsCleared, "через негорящую дверь не пройти");
            T.Run(s, 0.4f);
            Assert.AreEqual(1, s.RoomsCleared, "дверь загорелась — шаг в неё ведёт дальше");
        }

        [Test]
        public void EveryDoorStandsAtTheFloorEdgeFacingTheRoom()
        {
            foreach (var name in RoomLibrary.All)
            {
                var s = R.Room(name);
                foreach (var x in s.Room.exits)
                {
                    var at = s.DoorPlacement(x.pos, out var inward);
                    Assert.AreEqual(1f, inward.magnitude, 1e-3f, name);
                    var stand = s.DoorStandPoint(x.pos);
                    Assert.IsTrue(s.InsideRoom(stand, s.PlayerCfg.radius), $"{name}: у двери есть где стоять");
                    Assert.Greater(Vector2.Dot(stand - at, inward), -0.3f, $"{name}: место входа — перед стеной двери, со стороны её лица");
                    Assert.Less((stand - at).magnitude, 2f, $"{name}: дверь у места входа");
                }
            }
        }

        [Test]
        public void UrnsBreakAndStopBlocking()
        {
            var s = R.Room("A_Combat01");
            var b = s.Breakables[0];
            Assert.IsTrue(s.Obstacles.Contains(b.Body));
            s.HitRoomObjects(b.Pos, 0.3f);
            Assert.IsTrue(b.Broken);
            Assert.IsFalse(s.Obstacles.Contains(b.Body));
        }

        [Test]
        public void SpawnPointsMatchEnemyRole()
        {
            var s = new ArenaSim(1, RoomLibrary.Get("A_Combat01")) { MirrorRooms = false };
            T.Run(s, 0.1f);
            var thug = s.Enemies[0];
            var sp = System.Array.Find(s.Room.spawns, p => (p.pos - thug.Pos).sqrMagnitude < 0.01f);
            Assert.IsNotNull(sp);
            Assert.AreEqual("melee", sp.role);
        }
    }

    public class HitStunTests
    {
        [Test]
        public void CardHitInterruptsThugWindupButIsNotCc()
        {
            var s = T.NewSim();
            var e = T.Spawn(s, EnemyKind.Thug, new Vector2(0f, 1.4f));
            e.Aggro = true;
            s.Step(T.Dt, default);
            Assert.AreEqual(AiState.Windup, e.State);
            var slot = s.Slots[0];
            s.HitEnemy(e, new HitSource { Slot = slot, Card = slot.Card, Up = slot.Upgrade, Base = 10f });
            Assert.AreEqual(AiState.Chase, e.State, "OnHitStun 0.5 с срывает замах");
            Assert.AreEqual(0.5f, e.HitStun, 1e-4f);
            Assert.IsFalse(e.UnderCc, "это не контроль: бонусы против оглушённых не срабатывают");
            T.Run(s, 0.4f);
            Assert.AreEqual(s.PlayerCfg.hp, s.Player.Hp, 0.01f);
        }

        [Test]
        public void StrikeInProgressIsNotInterrupted()
        {
            var s = T.NewSim();
            var e = T.Spawn(s, EnemyKind.Thug, new Vector2(0f, 1.4f));
            e.Aggro = true;
            s.Step(T.Dt, default);
            T.Run(s, 0.55f);
            Assert.AreEqual(AiState.Strike, e.State);
            var slot = s.Slots[0];
            s.HitEnemy(e, new HitSource { Slot = slot, Card = slot.Card, Up = slot.Upgrade, Base = 10f });
            T.Run(s, 0.2f);
            Assert.Less(s.Player.Hp, s.PlayerCfg.hp, "начавшийся удар долетает");
        }
    }

    public class TrapTests
    {
        static bool RunUntil(ArenaSim s, float seconds, System.Func<bool> done, System.Action each = null)
        {
            int n = Mathf.RoundToInt(seconds / T.Dt);
            for (int i = 0; i < n; i++)
            {
                each?.Invoke();
                s.Step(T.Dt, default);
                if (done()) return true;
            }
            return false;
        }

        [Test]
        public void DartPlateFiresLinkedEmitter()
        {
            var s = R.Room("A_Combat04");
            var plate = R.First(s, TrapKind.DartPlate);
            Assert.IsNotNull(plate.Link, "пластина связана с излучателем");
            Assert.IsTrue(RunUntil(s, 1f, () => s.Bolts.Exists(b => b.FromTrap), () => s.Player.Pos = plate.Pos));
        }

        [Test]
        public void AxeTriggerSwingsChainedAxes()
        {
            var s = R.Room("D_Combat02");
            var trig = R.First(s, TrapKind.AxeTrigger);
            Assert.Greater(trig.Chain.Count, 0);
            Assert.IsTrue(RunUntil(s, 1f, () => trig.Chain.Exists(a => a.Phase != TrapPhase.Idle), () => s.Player.Pos = trig.Pos));
        }

        [Test]
        public void PhalanxStatueThrustsAtWhoeverStandsInFront()
        {
            var s = R.Room("C_Combat01");
            var t = R.First(s, TrapKind.Phalanx);
            var spot = t.Pos + t.Dir * Hades.U(150f);
            Assert.IsTrue(RunUntil(s, 3f, () => s.Player.Hp < s.PlayerCfg.hp, () => s.Player.Pos = spot), "укол копьём через 0.5 с замаха");
        }

        [Test]
        public void SawsAndArchersShootOnTheirOwn()
        {
            var saw = R.Room("D_Combat03");
            Assert.IsTrue(RunUntil(saw, 6f, () => saw.Bolts.Exists(b => b.Pierce)), "пила");
            var arch = R.Room("C_Combat05");
            Assert.IsTrue(RunUntil(arch, 8f, () => arch.Bolts.Exists(b => b.FromTrap)), "лучник");
        }

        [Test]
        public void GasAndPoisonWakeUpAndRelease()
        {
            var gas = R.Room("B_Combat02");
            Assert.IsTrue(RunUntil(gas, 25f, () => gas.Traps.Exists(t => t.Kind == TrapKind.Gas && t.Cloud > 0f)), "газ просыпается через 5–15 с, замах 3 с");
            var poison = R.Room("D_Mini05");
            Assert.IsTrue(RunUntil(poison, 25f, () => poison.Traps.Exists(t => t.Kind == TrapKind.Poison && t.Cloud > 0f)), "яд Стикса");
        }

        [Test]
        public void BlastCubeFusesAndExplodes()
        {
            var s = R.Room("A_Combat09");
            var cube = R.First(s, TrapKind.BlastCube);
            s.HitRoomObjects(cube.Pos, 0.3f);
            Assert.AreEqual(TrapPhase.Pre, cube.Phase, "удар поджигает фитиль");
            s.Player.Pos = cube.Pos + Vector2.right * 1.5f;
            Assert.IsTrue(RunUntil(s, 2f, () => cube.Phase == TrapPhase.Gone));
            Assert.Less(s.Player.Hp, s.PlayerCfg.hp, "рядом с кубом — урон от взрыва");
        }
    }
}
