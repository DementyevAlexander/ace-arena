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
            var g = gc.Sim.SpawnEnemy(EnemyKind.Ghost, p + new Vector2(0f, 2.4f));
            g.State = AiState.Chase; g.Cd = 0f;
            var st = gc.Sim.SpawnEnemy(EnemyKind.Stomper, p + new Vector2(1.5f, -0.5f));
            st.State = AiState.Chase; st.Cd = 0f;
            gc.Sim.Player.BuffInvuln = 5f;
            yield return new WaitForSeconds(0.35f);
            Assert.IsNotNull(g.Tele);
            yield return Capture("02_telegraphs");
            yield return new WaitForSeconds(1.5f);
            yield return Capture("03_after");
        }

        static IEnumerator Capture(string name)
        {
            var dir = System.Environment.GetEnvironmentVariable("ARENA_CAPTURE_DIR");
            if (string.IsNullOrEmpty(dir)) yield break;
            yield return new WaitForEndOfFrame();
            var tex = ScreenCapture.CaptureScreenshotAsTexture();
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, name + ".png"), tex.EncodeToPNG());
            Object.Destroy(tex);
        }
    }
}
