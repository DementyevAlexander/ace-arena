using Arena.Core;
using System.Collections;
using System.IO;
using Arena.Sim;
using Arena.View;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Arena.Tests
{
    /// <summary>Сцена Game запускается, волна появляется, обозначения атак рисуются. Кадры сохраняются, если задана
    /// переменная окружения ARENA_CAPTURE_DIR.</summary>
    public class GameSmokeTests
    {
        /// <summary>Эти тесты — про отдельные комнаты Hades: забег по этажам из арен (по умолчанию в игре) выключен.</summary>
        [SetUp]
        public void SingleRooms() => GameController.ArenaFloors = false;

        [UnityTest]
        public IEnumerator SceneRunsAndShowsTelegraphs()
        {
            // URP в batch-режиме пишет посторонние ошибки про RuntimeDebugWindow.uxml — к игре не относятся
            LogAssert.ignoreFailingMessages = true;
            if (System.Environment.GetEnvironmentVariable("ARENA_NO_SRP_BATCHER") == "1")
                UnityEngine.Rendering.GraphicsSettings.useScriptableRenderPipelineBatching = false;
            GameController.SkipTitle = true;
            SceneManager.LoadScene("Game");
            yield return null;
            var gc = Object.FindAnyObjectByType<GameController>();
            Assert.IsNotNull(gc);
            // режим форм: бой в первой комнате Тартара начинается сразу, карточка — в конце комнаты
            Assert.IsTrue(gc.Sim.FormsMode);
            Assert.IsFalse(gc.Sim.FloorMode, "этажи ETG выключены");
            Assert.IsFalse(gc.Sim.RewardPending);
            Assert.AreEqual("A_Combat01", gc.Sim.Room.name);
            yield return new WaitForSeconds(2.6f);
            Assert.Greater(gc.Sim.Wave, 0);
            Assert.Greater(gc.Sim.Enemies.Count, 0);
            yield return Capture("01_wave");

            // враги всех трёх игр рядом с героем: Wastrel и Casket (зоны на земле), Thug (белеет в замахе), ведьма, Ghost
            var p = gc.Sim.Player.Pos;
            gc.Sim.Player.BuffInvuln = 30f;
            foreach (var e in gc.Sim.Enemies) e.Pos = p + new Vector2(6f, 5f);
            var w = gc.Sim.SpawnEnemy(EnemyKind.Wastrel, p + new Vector2(0f, 2.4f));
            w.State = AiState.Chase; w.Cd = 0f;
            var cs = gc.Sim.SpawnEnemy(EnemyKind.Casket, p + new Vector2(2.5f, -0.5f));
            cs.State = AiState.Chase; cs.Cd = 0f;
            var thug = gc.Sim.SpawnEnemy(EnemyKind.Thug, p + new Vector2(-1.6f, 0.6f));
            thug.State = AiState.Chase; thug.T = 0f; thug.Aggro = true;
            var g = gc.Sim.SpawnEnemy(EnemyKind.Ghost, p + new Vector2(-1.5f, -2.4f));
            g.State = AiState.Chase; g.Cd = 0f;
            // ждём по игровому времени: в batch-режиме кадры медленные, а шаг кадра в симуляции ограничен
            for (int i = 0; i < 900 && thug.WarnT < 0.5f; i++) yield return null;
            Assert.Greater(thug.WarnT, 0f, "Thug в конце замаха белеет");
            yield return Capture("02_thug_windup");
            for (int i = 0; i < 900 && (w.Tele == null || w.Tele.Progress < 0.6f); i++) yield return null;
            Assert.IsNotNull(w.Tele);
            yield return Capture("03_telegraphs");
            yield return new WaitForSeconds(1.5f);
            yield return Capture("04_after");

            // формы: зона с пламенем, дух с молнией, очередь снарядов (ввод подаём прямо в симуляцию)
            var sim = gc.Sim;
            sim.Player.BuffInvuln = 5f;
            sim.Forms[1].Card = new SkillCard(FormKind.Zone, Trait.Fire);
            sim.Forms[2].Card = new SkillCard(FormKind.Summon, Trait.Lightning);
            sim.Forms[1].Reset(); sim.Forms[2].Reset();
            sim.Step(1f / 60f, new SimInput { Pressed = 1 << 1 });
            sim.Step(1f / 60f, new SimInput { Pressed = 1 << 2 });
            for (int i = 0; i < 20; i++) { sim.Step(1f / 60f, default); yield return null; }
            yield return Capture("05_zone_spirit");
            sim.Step(1f / 60f, new SimInput { Pressed = 1, Held = 1 });
            for (int i = 0; i < 40; i++) { sim.Step(1f / 60f, new SimInput { Held = 1 }); yield return null; }
            yield return Capture("06_shots");
            for (int i = 0; i < 30; i++) { sim.Step(1f / 60f, default); yield return null; }
        }

        /// <summary>Арена пройдена: надпись, потом загорается полотно двери (каркас не светится). Кадры — дверь до и после.</summary>
        [UnityTest]
        public IEnumerator DoorLightsUpAfterArenaCleared()
        {
            LogAssert.ignoreFailingMessages = true;
            GameController.SkipTitle = true;
            SceneManager.LoadScene("Game");
            yield return null;
            var gc = Object.FindAnyObjectByType<GameController>();
            foreach (var name in new[] { "A_Combat01", "B_Combat01" })
            {
                gc.LoadRoom(RoomLibrary.Get(name));
                var sim = gc.Sim;
                if (sim.RewardPending) sim.TakeOffer(0, sim.Offers[0].Form == FormKind.Dash ? ArenaSim.DashSlot : 1);
                var door = sim.DoorPlacement(sim.Room.exits[0].pos, out var inward);
                sim.Player.Pos = door + inward * 2.5f;
                sim.Player.BuffInvuln = 999f;
                for (int i = 0; i < 20; i++) yield return null;
                yield return Capture($"07_{name}_door_closed");
                bool cleared = false;
                for (int i = 0; i < 6000 && !sim.DoorsOpen; i++)
                {
                    foreach (var e in sim.Enemies) if (e.Alive) sim.Damage(e, 99999f, false);
                    if (sim.RewardPending) sim.FuseOffer(0, 0);
                    sim.Player.Pos = door + inward * 2.5f;
                    sim.Player.BuffInvuln = 999f;
                    foreach (var ev in sim.Events) if (ev.Kind == EventKind.ArenaCleared) cleared = true;
                    yield return null;
                }
                Assert.IsTrue(cleared, name + ": надпись «Арена пройдена»");
                Assert.IsTrue(sim.DoorsOpen, name + ": дверь загорелась");
                for (int i = 0; i < 10; i++) yield return null;
                yield return Capture($"08_{name}_door_open");
            }
        }

        /// <summary>Стартовый экран держит бой на паузе; «Новый забег» запускает его; пауза открывается и закрывается.
        /// Кадры с интерфейсом: стартовый экран, HUD, пауза.</summary>
        [UnityTest]
        public IEnumerator TitleHudAndPauseScreens()
        {
            LogAssert.ignoreFailingMessages = true;
            GameController.SkipTitle = false;
            SceneManager.LoadScene("Game");
            yield return null;
            var gc = Object.FindAnyObjectByType<GameController>();
            GameController.SkipTitle = true;
            for (int i = 0; i < 10; i++) yield return null;
            Assert.IsTrue(gc.TitleVisible, "при запуске — стартовый экран");
            Assert.IsTrue(gc.Paused, "на стартовом экране бой стоит");
            float t0 = gc.Sim.Time;
            yield return CaptureUi("09_title");
            Assert.AreEqual(t0, gc.Sim.Time, "симуляция не шла");

            gc.StartRun();
            yield return null;
            Assert.IsFalse(gc.TitleVisible);
            var sim = gc.Sim;
            sim.Player.BuffInvuln = 999f;
            for (int i = 0; i < 2000 && sim.Enemies.Count == 0; i++) yield return null;
            for (int i = 0; i < 60; i++) yield return null;
            Assert.IsFalse(gc.Paused);
            yield return CaptureUi("10_hud");

            gc.SetPauseMenu(true);
            yield return null;
            Assert.IsTrue(gc.Paused, "пауза открыта");
            t0 = sim.Time;
            for (int i = 0; i < 5; i++) yield return null;
            Assert.AreEqual(t0, sim.Time, "в паузе бой стоит");
            yield return CaptureUi("11_pause");
            gc.SetPauseMenu(false);
            yield return null;
            Assert.IsFalse(gc.Paused, "пауза закрыта");
        }

        /// <summary>Комната пройдена — карточка на земле; рядом с ней подсказка; подбор открывает выбор, «оставить» закрывает,
        /// «влить» применяет. Кадры: карточка на земле, выбор на занятом, пустом и рывковом слоте.</summary>
        [UnityTest]
        public IEnumerator LootOnGroundOpensChoice()
        {
            LogAssert.ignoreFailingMessages = true;
            GameController.SkipTitle = true;
            SceneManager.LoadScene("Game");
            yield return null;
            var gc = Object.FindAnyObjectByType<GameController>();
            var sim = gc.Sim;
            sim.Player.BuffInvuln = 999f;
            // слоты как в макете: Снаряд с Пламенем и душой Рикошета, Зона, пустой Q, Рывок
            sim.Forms[0].Card = new SkillCard(FormKind.Shot, Trait.Fire);
            sim.Forms[0].Card.Souls.Add(Trait.Ricochet);
            sim.Forms[1].Card = new SkillCard(FormKind.Zone);
            for (int i = 0; i < 6000 && sim.Loot.Count == 0; i++)
            {
                foreach (var e in sim.Enemies) if (e.Alive) sim.Damage(e, 99999f, false);
                yield return null;
            }
            Assert.AreEqual(2, sim.Loot.Count, "в конце комнаты — пятачок с двумя карточками");
            Assert.IsFalse(gc.Paused);
            sim.Loot[0].Card = new SkillCard(FormKind.Summon, Trait.Fire);
            sim.Loot[1].Card = new SkillCard(FormKind.Zone, Trait.Frost);
            sim.Player.Pos = sim.Loot[0].Pos - ArenaSim.ScreenRight * 0.3f + new Vector2(-0.6f, -0.6f); // перед карточкой
            for (int i = 0; i < 600 && sim.Time < sim.LootReadyAt + 0.3f; i++) yield return null;
            Assert.IsTrue(sim.LootInReach);
            yield return CaptureUi("12_loot_on_ground");

            Assert.IsTrue(sim.PickUpLoot());
            for (int i = 0; i < 3; i++) yield return null;
            Assert.IsTrue(gc.Paused, "выбор открыт — бой стоит");
            yield return CaptureUi("13_choice_occupied");
            sim.LeaveLoot();
            yield return null;
            Assert.IsFalse(gc.Paused);
            Assert.AreEqual(2, sim.Loot.Count, "оставленные карточки на пятачке");
            Assert.IsTrue(sim.PickUpLoot());
            yield return null;
            sim.FuseOffer(0, 0);
            for (int i = 0; i < 3; i++) yield return null;
            Assert.IsFalse(gc.Paused);
            Assert.AreEqual(0, sim.Loot.Count, "вторая сгорела");
            Assert.AreEqual(2, sim.Forms[0].Card.Level(Trait.Fire), "Пламя ×2");
        }

        /// <summary>Этаж из арен Hades (в игре по умолчанию): старт у входа; вход на арену запирает её решётками (решётка —
        /// препятствие); у мини-боссов и босса — красный враг с увеличенным здоровьем; зачистка открывает и даёт пятачок
        /// (у мини-босса — тринкеты); пройдя все бои и войдя в выход — второй этаж, тоже из арен, скиллы те же.
        /// Кадры: начало этажа, бой на арене, второй этаж.</summary>
        [UnityTest, Timeout(600000)]
        public IEnumerator ArenaFloorPlaysThroughToNextFloor()
        {
            LogAssert.ignoreFailingMessages = true;
            GameController.SkipTitle = true;
            GameController.ArenaFloors = true;
            SceneManager.LoadScene("Game");
            yield return null;
            GameController.ArenaFloors = false; // остальные тесты — комнаты Hades
            var gc = Object.FindAnyObjectByType<GameController>();
            var sim = gc.Sim;
            Assert.IsTrue(sim.HubMode, "игра начинается в хабе");
            yield return EnterGate(sim);
            Assert.IsTrue(sim.FloorMode && sim.Floor.Polygonal, "врата — забег по этажам из арен");
            Assert.AreEqual(sim.Floor.Entrance, sim.Floor.RoomAt(Mathf.FloorToInt(sim.Player.Pos.x), Mathf.FloorToInt(sim.Player.Pos.y)), "старт у входа");
            var shot = sim.Forms[0].Card;
            for (int i = 0; i < 30; i++) yield return null;
            yield return CaptureUi("23_arena_floor_start");
            sim.Player.BuffInvuln = 9999f;

            bool firstShot = false, sawMiniLoot = false;
            int elites = 0;
            for (int r = 0; r < sim.Floor.Rooms.Count; r++)
            {
                var room = sim.Floor.Rooms[r];
                if (!room.Combat) continue;
                string name = room.Arena?.Name ?? room.Room.name;
                // встать на пол комнаты: у арены — её точка появления врагов
                Vector2 at = default;
                bool found = false;
                if (room.ArenaSpawns != null)
                    foreach (var sp in room.ArenaSpawns)
                        if (sim.InsideRoom(sp.pos, 0.4f) && sim.Floor.RoomAt(Mathf.FloorToInt(sp.pos.x), Mathf.FloorToInt(sp.pos.y)) == r) { at = sp.pos; found = true; break; }
                if (!found) { var c = EtgFloorGenerator.FloorCellNear(sim.Floor, room.Center).Value; at = new Vector2(c.x + 0.5f, c.y + 0.5f); }
                sim.Player.Pos = at;
                for (int i = 0; i < 30 && sim.ActiveRoom != r; i++) yield return null;
                Assert.AreEqual(r, sim.ActiveRoom, name + ": вход запирает комнату");
                foreach (var d in room.Doors)
                    Assert.IsTrue(sim.HitsObstacle(new Vector2(d.Cells[0].x + 0.5f, d.Cells[0].y + 0.5f), 0.1f), name + ": решётка в двери");
                for (int i = 0; i < 400 && sim.Enemies.Count == 0; i++) yield return null;
                Assert.Greater(sim.Enemies.Count, 0, name + ": враги");
                if (room.Elite > 0)
                {
                    // мини-босс — красная элитка с двумя скиллами-формами, босс — Мегера
                    var elite = sim.Enemies.Find(e => room.Elite == 2 ? e.Kind == EnemyKind.Megaera : e.Elite);
                    Assert.IsNotNull(elite, name + (room.Elite == 2 ? ": Мегера" : ": красный враг"));
                    if (room.Elite == 1) Assert.AreEqual(2, elite.Skills?.Length ?? 0, name + ": два скилла у мини-босса");
                    elites++;
                }
                if (!firstShot) { firstShot = true; for (int i = 0; i < 90; i++) yield return null; sim.Player.BuffInvuln = 0f; yield return CaptureUi("24_arena_fight"); sim.Player.BuffInvuln = 9999f; }
                for (int i = 0; i < 6000 && sim.ActiveRoom == r; i++)
                {
                    foreach (var e in sim.Enemies) if (e.Alive) sim.Damage(e, 99999f, false);
                    if (sim.RewardPending) sim.LeaveLoot();
                    yield return null;
                }
                Assert.AreEqual(ArenaSim.FloorRoomState.Cleared, sim.RoomStates[r], name + ": пройдена");
                Assert.IsFalse(sim.HitsObstacle(new Vector2(room.Doors[0].Cells[0].x + 0.5f, room.Doors[0].Cells[0].y + 0.5f), 0.1f), name + ": решётки подняты");
                Assert.IsTrue(sim.Pedestal, name + ": награда после боя");
                if (room.Elite == 1) { Assert.IsTrue(sim.Loot.TrueForAll(g => g.Trinket != null), name + ": у мини-босса — тринкеты"); sawMiniLoot = true; }
            }
            Assert.AreEqual(3, elites, "два мини-босса и босс");
            Assert.IsTrue(sawMiniLoot);
            var (cleared, total) = sim.FloorProgress;
            Assert.AreEqual(total, cleared);
            Assert.GreaterOrEqual(sim.Floor.Exit, 0, "у этажа есть выход");
            sim.Player.Pos = sim.Floor.Rooms[sim.Floor.Exit].Center;
            for (int i = 0; i < 30 && sim.FloorIndex == 0; i++) yield return null;
            Assert.AreEqual(1, sim.FloorIndex, "выход ведёт на второй этаж");
            Assert.IsTrue(sim.Floor.Polygonal, "второй этаж — тоже из арен");
            Assert.AreSame(shot, sim.Forms[0].Card, "скиллы переходят на следующий этаж");
            sim.Player.BuffInvuln = 0f;
            for (int i = 0; i < 30; i++) yield return null;
            yield return CaptureUi("25_arena_floor2");
        }

        /// <summary>Встать во врата хаба и нажать «взаимодействие» — забег начинается после шага.</summary>
        static IEnumerator EnterGate(ArenaSim sim)
        {
            var gate = sim.HubSpots.Find(x => x.Kind == HubSpotKind.Gate);
            Assert.IsNotNull(gate, "в хабе есть врата");
            sim.Player.Pos = gate.Pos;
            Assert.IsTrue(sim.Interact(), "врата отвечают на E");
            for (int i = 0; i < 10 && sim.HubMode; i++) yield return null;
        }

        /// <summary>Хаб (в игре — старт): персонажи говорят, мишени не умирают; врата — забег; смерть — итоги; «В хаб» —
        /// снова хаб, билд с нуля. Кадры: хаб, итоги.</summary>
        [UnityTest]
        public IEnumerator HubLeadsToRunAndBack()
        {
            LogAssert.ignoreFailingMessages = true;
            GameController.SkipTitle = true;
            GameController.ArenaFloors = true;
            SceneManager.LoadScene("Game");
            yield return null;
            GameController.ArenaFloors = false;
            var gc = Object.FindAnyObjectByType<GameController>();
            var sim = gc.Sim;
            Assert.IsTrue(sim.HubMode);
            Assert.AreEqual(4, sim.HubSpots.FindAll(x => x.Kind == HubSpotKind.Npc).Count, "четыре места под персонажей");
            Assert.AreEqual(3, sim.Enemies.FindAll(e => e.Dummy).Count, "три мишени");
            for (int i = 0; i < 30; i++) yield return null;
            // мишень не умирает и не ходит
            var dummy = sim.Enemies.Find(e => e.Dummy);
            var home = dummy.Pos;
            sim.Damage(dummy, 1e6f, false);
            for (int i = 0; i < 30; i++) yield return null;
            Assert.IsFalse(dummy.Dead, "мишень не умирает");
            Assert.Less((dummy.Pos - home).magnitude, 0.01f, "мишень стоит");
            // персонаж отвечает репликой
            var npc = sim.HubSpots.Find(x => x.Kind == HubSpotKind.Npc);
            sim.Player.Pos = npc.Pos + (sim.Room.heroStart - npc.Pos).normalized * 1.2f;
            Assert.IsTrue(sim.Interact());
            Assert.AreSame(npc, sim.SpeechSpot);
            Assert.IsFalse(string.IsNullOrEmpty(sim.SpeechText));
            for (int i = 0; i < 20; i++) yield return null;
            yield return CaptureUi("26_hub");

            // врата → забег; карта забега меняется — в хабе снова старт
            yield return EnterGate(sim);
            Assert.IsTrue(sim.FloorMode && !sim.HubMode, "врата — забег");
            sim.Forms[1].Card = new SkillCard(FormKind.Zone, Trait.Fire);
            sim.Player.BuffInvuln = 0f; sim.Player.HitInvuln = 0f;
            sim.HurtPlayer(99999f, sim.Player.Pos + Vector2.right);
            for (int i = 0; i < 10 && !gc.RunEndVisible; i++) yield return null;
            Assert.IsTrue(sim.GameOver);
            Assert.IsTrue(gc.RunEndVisible, "смерть — итоги забега");
            yield return CaptureUi("27_run_end");
            gc.ConfirmRunEnd();
            yield return null;
            Assert.IsTrue(sim.HubMode, "итоги — снова хаб");
            Assert.IsFalse(sim.GameOver);
            Assert.IsNull(sim.Forms[1].Card, "билд с нуля");
        }

        /// <summary>Этаж Enter the Gungeon (в игре выключен, тест включает его на время загрузки сцены): старт у входа; вход в боевую комнату запирает двери и даёт волны; зачистка открывает;
        /// пройдя все боевые комнаты и войдя в выход — следующий этаж. Кадры: начало этажа, запертая комната, второй этаж.</summary>
        [UnityTest]
        public IEnumerator FloorPlaysThroughToNextFloor()
        {
            LogAssert.ignoreFailingMessages = true;
            GameController.SkipTitle = true;
            GameController.EtgFloors = true;
            SceneManager.LoadScene("Game");
            yield return null;
            GameController.EtgFloors = false; // остальные тесты — комнаты Hades
            var gc = Object.FindAnyObjectByType<GameController>();
            var sim = gc.Sim;
            Assert.IsTrue(sim.FloorMode, "забег — этажи");
            Assert.AreEqual(0, sim.FloorIndex);
            Assert.AreEqual(sim.Floor.Entrance, sim.Floor.RoomAt(Mathf.FloorToInt(sim.Player.Pos.x), Mathf.FloorToInt(sim.Player.Pos.y)), "старт у входа");
            for (int i = 0; i < 30; i++) yield return null;
            yield return CaptureUi("20_floor_start"); // до бессмертия: в нём модель мигает и может не попасть в кадр
            sim.Player.BuffInvuln = 9999f;

            bool firstShot = false;
            for (int r = 0; r < sim.Floor.Rooms.Count; r++)
            {
                var room = sim.Floor.Rooms[r];
                if (!room.Combat) continue;
                var cell = EtgFloorGenerator.FloorCellNear(sim.Floor, room.Center).Value;
                sim.Player.Pos = new Vector2(cell.x + 0.5f, cell.y + 0.5f);
                for (int i = 0; i < 20 && sim.ActiveRoom != r; i++) yield return null;
                Assert.AreEqual(r, sim.ActiveRoom, room.Room.name + ": вход запирает комнату");
                for (int i = 0; i < 400 && sim.Enemies.Count == 0; i++) yield return null;
                Assert.Greater(sim.Enemies.Count, 0, room.Room.name + ": волна");
                if (!firstShot) { firstShot = true; for (int i = 0; i < 90; i++) yield return null; sim.Player.BuffInvuln = 0f; yield return CaptureUi("21_room_locked"); sim.Player.BuffInvuln = 9999f; }
                for (int i = 0; i < 6000 && sim.ActiveRoom == r; i++)
                {
                    foreach (var e in sim.Enemies) if (e.Alive) sim.Damage(e, 99999f, false);
                    if (sim.RewardPending) sim.LeaveLoot();
                    yield return null;
                }
                Assert.AreEqual(ArenaSim.FloorRoomState.Cleared, sim.RoomStates[r], room.Room.name + ": пройдена");
                Assert.AreEqual(-1, sim.ActiveRoom, "двери открыты");
            }
            var (cleared, total) = sim.FloorProgress;
            Assert.AreEqual(total, cleared);
            Assert.GreaterOrEqual(sim.Floor.Exit, 0, "у первого этажа есть выход");
            var ex = sim.Floor.Rooms[sim.Floor.Exit].Center;
            sim.Player.Pos = ex;
            for (int i = 0; i < 30 && sim.FloorIndex == 0; i++) yield return null;
            Assert.AreEqual(1, sim.FloorIndex, "выход ведёт на второй этаж");
            sim.Player.BuffInvuln = 0f;
            for (int i = 0; i < 30; i++) yield return null;
            yield return CaptureUi("22_floor2");
        }

        /// <summary>Кадр вместе с интерфейсом: экранные холсты на время снимка переводятся на камеру.</summary>
        static IEnumerator CaptureUi(string name)
        {
            var dir = System.Environment.GetEnvironmentVariable("ARENA_CAPTURE_DIR");
            if (string.IsNullOrEmpty(dir)) yield break;
            yield return null;
            var cam = Camera.main;
            var rt = new RenderTexture(1920, 1080, 24);
            var prev = cam.targetTexture;
            cam.targetTexture = rt;
            var volume = Object.FindAnyObjectByType<UnityEngine.Rendering.Volume>(); // постобработка не должна трогать интерфейс
            if (volume != null) volume.enabled = false;
            var camData = UnityEngine.Rendering.Universal.CameraExtensions.GetUniversalAdditionalCameraData(cam);
            var aa = camData.antialiasing;
            camData.antialiasing = UnityEngine.Rendering.Universal.AntialiasingMode.None; // SMAA размазывает текст на холстах камеры
            var canvases = Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None);
            foreach (var c in canvases)
            {
                c.renderMode = RenderMode.ScreenSpaceCamera;
                c.worldCamera = cam;
                c.planeDistance = cam.nearClipPlane + 0.05f + 0.01f * (100 - c.sortingOrder);
            }
            Canvas.ForceUpdateCanvases();
            cam.Render();
            foreach (var c in canvases) { c.renderMode = RenderMode.ScreenSpaceOverlay; c.worldCamera = null; }
            if (volume != null) volume.enabled = true;
            camData.antialiasing = aa;
            cam.targetTexture = prev;
            var old = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = old;
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, name + ".png"), tex.EncodeToPNG());
            Object.Destroy(tex);
            rt.Release();
        }

        static IEnumerator Capture(string name)
        {
            var dir = System.Environment.GetEnvironmentVariable("ARENA_CAPTURE_DIR");
            if (string.IsNullOrEmpty(dir)) yield break;
            yield return null;
            // В batch-режиме WaitForEndOfFrame не приходит — рендерим камеру в текстуру сами (без экранного интерфейса)
            var cam = Camera.main;
            var rt = new RenderTexture(1600, 900, 24);
            var prev = cam.targetTexture;
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = prev;
            var old = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = old;
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, name + ".png"), tex.EncodeToPNG());
            Object.Destroy(tex);
            rt.Release();
        }
    }
}
