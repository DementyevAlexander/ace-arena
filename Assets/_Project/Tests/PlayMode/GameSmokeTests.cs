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
            SceneManager.LoadScene("Game");
            yield return null;
            var gc = Object.FindFirstObjectByType<GameController>();
            Assert.IsNotNull(gc);
            yield return new WaitForSeconds(2.6f);
            Assert.Greater(gc.Sim.Wave, 0);
            Assert.Greater(gc.Sim.Enemies.Count, 0);
            yield return Capture("01_wave");

            // ставим призрака и топотуна рядом с Ace и даём начать замах — проверяем обозначения
            var p = gc.Sim.Player.Pos;
            foreach (var e in gc.Sim.Enemies) e.Pos = p + new Vector2(6f, 5f);
            var g = gc.Sim.SpawnEnemy(EnemyKind.Wastrel, p + new Vector2(0f, 2.4f));
            g.State = AiState.Chase; g.Cd = 0f;
            var st = gc.Sim.SpawnEnemy(EnemyKind.Casket, p + new Vector2(1.5f, -0.5f));
            st.State = AiState.Chase; st.Cd = 0f;
            gc.Sim.Player.BuffInvuln = 5f;
            // ждём по игровому времени: в batch-режиме кадры медленные, а шаг кадра в симуляции ограничен
            for (int i = 0; i < 600 && (g.Tele == null || g.Tele.Progress < 0.6f); i++) yield return null;
            Assert.IsNotNull(g.Tele);
            yield return Capture("02_telegraphs");
            yield return new WaitForSeconds(1.5f);
            yield return Capture("03_after");
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
