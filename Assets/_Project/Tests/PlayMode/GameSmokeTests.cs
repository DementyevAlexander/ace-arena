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
            // режим форм: игра начинается с выбора карточки на улице Эфиры
            Assert.IsTrue(gc.Sim.FormsMode);
            Assert.IsTrue(gc.Sim.RewardPending);
            Assert.IsTrue(gc.Sim.Room.IsHades2);
            yield return Capture("00_reward");
            gc.Sim.TakeOffer(0, gc.Sim.Offers[0].Form == FormKind.Dash ? ArenaSim.DashSlot : 1);
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
            foreach (var name in new[] { "A_Combat01", "N_Combat06" })
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
            sim.TakeOffer(0, sim.Offers[0].Form == FormKind.Dash ? ArenaSim.DashSlot : 1);
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
