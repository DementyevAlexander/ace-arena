using System.Collections;
using System.IO;
using Arena.Core;
using Arena.View;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Arena.Tests
{
    /// <summary>Все 30 комнат по очереди: загрузка, 4 с игры с волнами и ловушками, кадры — общий план и вид как в Hades.
    /// Кадры пишутся, только если задана ARENA_CAPTURE_DIR; без неё тест просто проверяет, что комнаты играются.</summary>
    public class RoomGalleryTests
    {
        /// <summary>Эти тесты — про отдельные комнаты Hades: забег по этажам из арен (по умолчанию в игре) выключен.</summary>
        [SetUp]
        public void SingleRooms() => GameController.ArenaFloors = false;

        [UnityTest, Timeout(1800000)] // 30 комнат по 4 с игры
        public IEnumerator EveryRoomPlays()
        {
            LogAssert.ignoreFailingMessages = true;
            GameController.SkipTitle = true;
            SceneManager.LoadScene("Game");
            yield return null;
            var gc = Object.FindAnyObjectByType<GameController>();
            Assert.IsNotNull(gc);
            var dir = System.Environment.GetEnvironmentVariable("ARENA_CAPTURE_DIR");
            foreach (var name in RoomLibrary.All)
            {
                gc.LoadRoom(RoomLibrary.Get(name));
                gc.Sim.Player.BuffInvuln = 999f;
                float t0 = gc.Sim.Time;
                for (int i = 0; i < 2000 && gc.Sim.Time - t0 < 4f; i++) yield return null;
                Assert.IsFalse(gc.Sim.GameOver, name);
                Assert.IsTrue(gc.Sim.InsideRoom(gc.Sim.Player.Pos, 0f), name);
                if (string.IsNullOrEmpty(dir)) continue;
                Capture(gc, Path.Combine(dir, "rooms", name + "_view.png"), false);
                Capture(gc, Path.Combine(dir, "rooms", name + "_plan.png"), true);
            }
        }

        static void Capture(GameController gc, string path, bool overview)
        {
            var cam = gc.Rig.Cam;
            var pos = cam.transform.position;
            float size = cam.orthographicSize;
            if (overview)
            {
                var r = gc.Sim.Room;
                var c = (r.min + r.max) * 0.5f;
                var ext = r.max - r.min;
                cam.orthographicSize = Mathf.Max(ext.x, ext.y) * 0.42f;
                cam.transform.position = new Vector3(c.x, 0f, c.y) - cam.transform.forward * CameraRig.Distance * 3f;
                cam.farClipPlane = 400f;
            }
            var rt = new RenderTexture(1280, 720, 24);
            cam.targetTexture = rt;
            cam.Render();
            cam.targetTexture = null;
            var old = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            tex.Apply();
            RenderTexture.active = old;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, tex.EncodeToPNG());
            Object.Destroy(tex);
            rt.Release();
            cam.orthographicSize = size;
            cam.transform.position = pos;
            cam.farClipPlane = 80f;
        }
    }
}
