using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Arena.Core;
using Arena.Sim;
using Arena.View;
using NUnit.Framework;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace Arena.Tests
{
    /// <summary>Нагрузочный прогон сцены: тяжёлые скиллы, толпа врагов, непрерывная стрельба. Пишет отчёт по кадрам
    /// (симуляция, вид, HUD, отрисовка, аллокации, вызовы отрисовки, число объектов и материалов) в ARENA_PERF_FILE.</summary>
    public class PerfTests
    {
        /// <summary>Эти тесты — про отдельные комнаты Hades: забег по этажам из арен (по умолчанию в игре) выключен.</summary>
        [SetUp]
        public void SingleRooms()
        {
            GameController.ArenaFloors = false;
            GameController.MetaFile = null; // без сохранения: всё открыто, файл игрока не трогаем
        }

        sealed class Phase
        {
            public string Name;
            public readonly List<float> Sim = new(), Views = new(), Hud = new(), Render = new(), Gpu = new(), Frame = new(), Gc = new(), Batches = new(), SetPass = new();
        }

        [UnityTest]
        public IEnumerator HeavyFight()
        {
            LogAssert.ignoreFailingMessages = true;
            GameController.SkipTitle = true;
            SceneManager.LoadScene("Game");
            yield return null;
            var gc = Object.FindAnyObjectByType<GameController>();
            var sim = gc.Sim;
            yield return null;

            var cam = Camera.main;
            var rt = new RenderTexture(1920, 1080, 24);
            var report = new StringBuilder();
            report.AppendLine($"GPU: {SystemInfo.graphicsDeviceName}, {SystemInfo.graphicsDeviceType}; CPU: {SystemInfo.processorType}");
            int mats0 = Resources.FindObjectsOfTypeAll<Material>().Length, gos0 = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include).Length;
            int meshes0 = Resources.FindObjectsOfTypeAll<Mesh>().Length;

            using var gcRec = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");
            using var batchRec = ProfilerRecorder.StartNew(ProfilerCategory.Render, "Batches Count");
            using var setPassRec = ProfilerRecorder.StartNew(ProfilerCategory.Render, "SetPass Calls Count");
            var frameWatch = new System.Diagnostics.Stopwatch();
            var renderWatch = new System.Diagnostics.Stopwatch();

            IEnumerator Run(Phase ph, int frames)
            {
                for (int i = 0; i < frames; i++)
                {
                    frameWatch.Restart();
                    yield return null;
                    // в batch-режиме камера сама не рисует — рисуем вручную, чтобы учесть цену отрисовки на CPU
                    renderWatch.Restart();
                    cam.targetTexture = rt; cam.Render(); cam.targetTexture = null;
                    float cpuRender = (float)renderWatch.Elapsed.TotalMilliseconds;
                    // то же с ожиданием видеокарты: читаем один пиксель — запрос ждёт, пока GPU дорисует кадр
                    AsyncGPUReadback.Request(rt, 0, 0, 1, 0, 1, 0, 1).WaitForCompletion();
                    renderWatch.Stop();
                    ph.Gpu.Add((float)renderWatch.Elapsed.TotalMilliseconds);
                    ph.Frame.Add((float)frameWatch.Elapsed.TotalMilliseconds);
                    ph.Sim.Add(gc.SimMs); ph.Views.Add(gc.ViewsMs); ph.Hud.Add(gc.HudMs);
                    ph.Render.Add(cpuRender);
                    ph.Gc.Add(gcRec.LastValue / 1024f); ph.Batches.Add(batchRec.LastValue); ph.SetPass.Add(setPassRec.LastValue);
                }
            }

            // 1. Спокойно: первая волна, никто не стреляет
            sim.Player.BuffInvuln = 999f;
            var idle = new Phase { Name = "Спокойно (первая волна)" };
            gc.TestInput = () => default;
            yield return Run(idle, 240);

            // 2. Бой: тяжёлые скиллы и 12 врагов
            sim.WavesEnabled = false;
            var shot = new SkillCard(FormKind.Shot, Trait.Split); shot.Souls.Add(Trait.Ricochet); shot.Souls.Add(Trait.Echo);
            var zone = new SkillCard(FormKind.Zone, Trait.Fire); zone.Souls.Add(Trait.Lightning);
            var spirit = new SkillCard(FormKind.Summon, Trait.Lightning); spirit.Souls.Add(Trait.Split);
            var dash = new SkillCard(FormKind.Dash, Trait.Fire);
            sim.Forms[0].Card = shot; sim.Forms[1].Card = zone; sim.Forms[2].Card = spirit; sim.Forms[3].Card = dash;
            foreach (var f in sim.Forms) f.Reset();
            var center = sim.Player.Pos;
            for (int k = 0; k < 12; k++)
            {
                float a = k * Mathf.PI * 2f / 12f;
                var e = sim.SpawnEnemy(k % 3 == 0 ? EnemyKind.Casket : EnemyKind.Wastrel, center + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * 4.5f);
                e.State = AiState.Chase; e.T = 0f;
                e.Hp = e.MaxHp = 1e7f; // не умирают — нагрузка не спадает
            }
            int frame = 0;
            gc.TestInput = () =>
            {
                frame++;
                var inp = new SimInput { Held = 1, Move = new Vector2(Mathf.Sin(frame * 0.02f), Mathf.Cos(frame * 0.013f)) * 0.0f };
                if (frame == 1) inp.Pressed |= 1;
                if (frame % 200 == 5) inp.Pressed |= 1 << 1;
                if (frame % 600 == 9) inp.Pressed |= 1 << 2;
                if (frame % 90 == 45) { inp.Pressed |= 1 << 3; inp.Move = new Vector2(Mathf.Cos(frame), Mathf.Sin(frame)); }
                sim.Player.BuffInvuln = 999f;
                return inp;
            };
            var fight = new Phase { Name = "Бой: 12 врагов, снаряд Раздвоение+Рикошет+Эхо, зона Пламя+Молния, 2 духа с молнией" };
            yield return Run(fight, 60 * 8);

            // 3. Цена эффектов отрисовки: в том же бою выключаем по одному
            var urp = (UniversalRenderPipelineAsset)GraphicsSettings.currentRenderPipeline;
            var dataList = (ScriptableRendererData[])typeof(UniversalRenderPipelineAsset)
                .GetField("m_RendererDataList", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(urp);
            var ssao = dataList[0].rendererFeatures.FirstOrDefault(f => f.name.Contains("Ambient") || f.GetType().Name.Contains("Occlusion"));
            var vol = Object.FindAnyObjectByType<Volume>();
            vol.profile.TryGet<DepthOfField>(out var dof);
            vol.profile.TryGet<Bloom>(out var bloom);
            var sun = Object.FindAnyObjectByType<Light>(); // направленный свет — первый в сцене
            foreach (var l in Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude)) if (l.type == LightType.Directional) sun = l;
            var camData = cam.GetUniversalAdditionalCameraData();
            var aa = camData.antialiasing;
            var toggles = new List<(string, System.Action<bool>)>
            {
                ("без SSAO", on => ssao?.SetActive(on)),
                ("без глубины резкости (DOF)", on => dof.active = on),
                ("без bloom", on => bloom.active = on),
                ("без теней солнца", on => sun.shadows = on ? LightShadows.Soft : LightShadows.None),
                ("без SMAA", on => camData.antialiasing = on ? aa : AntialiasingMode.None),
                ("без всей постобработки", on => camData.renderPostProcessing = on),
            };
            var gpuPhases = new List<Phase>();
            var baseline = new Phase { Name = "всё включено" };
            yield return Run(baseline, 120);
            gpuPhases.Add(baseline);
            foreach (var (name, set) in toggles)
            {
                set(false);
                var ph = new Phase { Name = name };
                yield return Run(ph, 120);
                gpuPhases.Add(ph);
                set(true);
            }
            report.AppendLine("\n== Цена отрисовки (тот же бой, по 120 кадров): отрисовка + ожидание GPU, мс");
            foreach (var ph in gpuPhases)
                report.AppendLine($"  {ph.Name,-30} среднее {ph.Gpu.Average(),7:0.00}   (CPU-часть {ph.Render.Average(),6:0.00})");

            int mats1 = Resources.FindObjectsOfTypeAll<Material>().Length, gos1 = Object.FindObjectsByType<Transform>(FindObjectsInactive.Include).Length;
            int meshes1 = Resources.FindObjectsOfTypeAll<Mesh>().Length;
            foreach (var ph in new[] { idle, fight })
            {
                report.AppendLine($"\n== {ph.Name} ({ph.Frame.Count} кадров)");
                Line(report, "кадр целиком, мс", ph.Frame);
                Line(report, "симуляция, мс", ph.Sim);
                Line(report, "вид (меши, эффекты), мс", ph.Views);
                Line(report, "HUD, мс", ph.Hud);
                Line(report, "отрисовка камеры (CPU), мс", ph.Render);
                Line(report, "отрисовка + ожидание GPU, мс", ph.Gpu);
                Line(report, "GC за кадр, КБ", ph.Gc);
                Line(report, "Batches", ph.Batches);
                Line(report, "SetPass", ph.SetPass);
            }
            report.AppendLine($"\nЭффектов сейчас: {sim.Effects.Count}, врагов: {sim.Enemies.Count}");
            report.AppendLine($"Материалов: {mats0} → {mats1}, мешей: {meshes0} → {meshes1}, объектов (Transform): {gos0} → {gos1}");
            var path = System.Environment.GetEnvironmentVariable("ARENA_PERF_FILE");
            if (!string.IsNullOrEmpty(path)) File.WriteAllText(path, report.ToString());
            Debug.Log(report.ToString());
            rt.Release();
        }

        static void Line(StringBuilder sb, string name, List<float> v)
        {
            var s = v.OrderBy(x => x).ToList();
            float avg = v.Average();
            float p95 = s[Mathf.Min(s.Count - 1, (int)(s.Count * 0.95f))];
            sb.AppendLine($"  {name,-30} среднее {avg,8:0.00}   95% {p95,8:0.00}   макс {s[^1],8:0.00}");
        }
    }
}
