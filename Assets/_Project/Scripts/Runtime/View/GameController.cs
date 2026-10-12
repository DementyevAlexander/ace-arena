using System.Collections.Generic;
using Arena.Core;
using Arena.Sim;
using Arena.UI;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Arena.View
{
    /// <summary>Тонкий слой: шаги симуляции с фиксированным dt, ввод, синхронизация вида, события → вспышки, цифры, стоп-кадр.
    /// Сцена содержит только этот компонент; камера, свет, Volume, комната и интерфейс создаются из кода.</summary>
    public sealed class GameController : MonoBehaviour
    {
        const float Step = 1f / 60f;

        public ArenaSim Sim { get; private set; }
        public CameraRig Rig { get; private set; }
        public bool Paused { get; private set; }
        /// <summary>Тесты: подменяет ввод игрока (null — клавиатура, мышь, геймпад).</summary>
        public System.Func<SimInput> TestInput;
        /// <summary>Время частей прошлого кадра, мс — для нагрузочного теста (то же видно в Profiler по маркерам Arena.*).</summary>
        public float SimMs, ViewsMs, HudMs;

        static readonly ProfilerMarker SimMarker = new("Arena.Sim"), ViewsMarker = new("Arena.Views"), HudMarker = new("Arena.Hud");
        readonly System.Diagnostics.Stopwatch watch = new();

        readonly InputReader input = new();
        readonly Dictionary<int, EnemyView> enemyViews = new();
        readonly List<int> stale = new();
        PlayerView player;
        EffectViews effects;
        Hud hud;
        RewardScreen reward; // выбор карточки и просмотр скиллов; прежняя колода (DeckScreen) из интерфейса убрана
        bool viewSkills, pauseMenu;
        Transform world, lockMarker;
        float acc, time;
        // хитстоп (HitSimSlowParameters): шаги профиля, время с начала, длительность; новый не начинается, пока идёт прежний
        float[] slowSteps; float slowT = -1f, slowTotal;
        readonly List<Vector3> pendingShakes = new(); // (через сколько, Distance, Duration) — ScreenPreWait тряски
        float zoomK; // 0…1 — приближение Ω-заряда
        bool wasGameOver;

        void Awake()
        {
            Application.targetFrameRate = 120;
            // подключённая арена — Resources/Arenas/index.json (active); без неё — классическая комната
            Sim = new ArenaSim(System.Environment.TickCount, layout: ArenaCatalog.LoadActive());
            Sim.EnableForms(); // скиллы — формы со свойствами; посох и карты DandyAce остаются в коде, но не предлагаются
            world = new GameObject("World").transform;
            bool night = Sim.Layout != null; // арены Hades — ночной город, классическая комната — дневная поляна
            SetupCamera(night);
            SetupLight(night);
            SetupVolume(night);
            RoomView.Build(Sim).SetParent(world, false);
            player = new PlayerView(world);
            effects = new EffectViews(world);
            var lm = Art.DecalInstance(new Color(1f, 0.95f, 0.6f, 0.85f), 1, 7);
            lockMarker = Art.Decal(world, MeshKit.FloorQuad(), lm, 0.06f).transform;
            hud = new Hud();
            reward = new RewardScreen(Sim);
            Rig.Snap(Art.W(Sim.Player.Pos));
            if (Sim.Layout != null) hud.ShowBanner(Sim.Layout.title, 2f);
        }

        /// <summary>Проверка билда без участия человека: -arenaShot &lt;файл.png&gt; — взять первую карточку, через 6 с сохранить
        /// кадр (с интерфейсом) и выйти.</summary>
        System.Collections.IEnumerator Start()
        {
            var args = System.Environment.GetCommandLineArgs();
            string shot = null;
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "-arenaShot") shot = args[i + 1];
            if (shot == null) yield break;
            yield return new WaitForSecondsRealtime(1.5f);
            if (Sim.RewardPending) Sim.TakeOffer(0, Sim.Offers[0].Form == FormKind.Dash ? ArenaSim.DashSlot : 1);
            yield return new WaitForSecondsRealtime(6f);
            ScreenCapture.CaptureScreenshot(shot);
            yield return new WaitForSecondsRealtime(1f);
            Application.Quit();
        }

        void SetupCamera(bool night)
        {
            var cam = Camera.main;
            if (cam == null)
            {
                var go = new GameObject("Main Camera") { tag = "MainCamera" };
                cam = go.AddComponent<Camera>();
            }
            Rig = new CameraRig(cam);
            if (night) cam.backgroundColor = Art.Night.Sky;
            if (Sim.Layout != null) Rig.SetHadesZoom(Sim.Layout.zoom);
            var data = cam.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = true;
            data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
        }

        static void SetupLight(bool night)
        {
            var sun = FindFirstObjectByType<Light>();
            if (sun == null) sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
            if (night)
            {
                // Луна: холодный слабый свет, тени мягкие. Основной свет — тёплые жаровни комнаты (RoomView).
                sun.color = new Color(0.55f, 0.6f, 0.9f);
                sun.intensity = 0.42f;
                sun.shadows = LightShadows.Soft;
                sun.shadowStrength = 0.8f;
                sun.transform.rotation = Quaternion.Euler(58f, -30f, 0f);
                RenderSettings.ambientMode = AmbientMode.Trilight;
                RenderSettings.ambientSkyColor = new Color(0.20f, 0.17f, 0.27f);
                RenderSettings.ambientEquatorColor = new Color(0.15f, 0.11f, 0.13f);
                RenderSettings.ambientGroundColor = new Color(0.06f, 0.05f, 0.06f);
                RenderSettings.fog = false;
                return;
            }
            sun.color = new Color(1f, 0.93f, 0.82f);
            sun.intensity = 1.35f;
            sun.shadows = LightShadows.Soft;
            sun.shadowStrength = 0.75f;
            sun.transform.rotation = Quaternion.Euler(52f, 20f, 0f);
            // Мягкий трёхцветный эмбиент: небо голубое, низ тёплый — тени не чёрные, как в TUNIC
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.62f, 0.72f, 0.86f);
            RenderSettings.ambientEquatorColor = new Color(0.66f, 0.62f, 0.58f);
            RenderSettings.ambientGroundColor = new Color(0.42f, 0.36f, 0.34f);
            RenderSettings.fog = false;
        }

        void SetupVolume(bool night)
        {
            var go = new GameObject("PostFX");
            var vol = go.AddComponent<Volume>();
            vol.isGlobal = true;
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            var bloom = profile.Add<Bloom>(true);
            bloom.intensity.Override(0.7f); bloom.threshold.Override(0.95f); bloom.scatter.Override(0.65f);
            var tone = profile.Add<Tonemapping>(true);
            tone.mode.Override(TonemappingMode.Neutral);
            var color = profile.Add<ColorAdjustments>(true);
            if (night)
            {
                bloom.intensity.Override(1.1f); bloom.threshold.Override(0.85f);
                color.saturation.Override(4f); color.contrast.Override(16f); color.postExposure.Override(0.5f);
                color.colorFilter.Override(new Color(1f, 0.93f, 0.92f));
            }
            else
            {
                color.saturation.Override(12f); color.contrast.Override(6f); color.postExposure.Override(0.15f);
                color.colorFilter.Override(new Color(1f, 0.98f, 0.95f));
            }
            var vig = profile.Add<Vignette>(true);
            vig.intensity.Override(night ? 0.4f : 0.22f); vig.smoothness.Override(0.5f);
            if (night) vig.color.Override(new Color(0.08f, 0.0f, 0.04f));
            // Лёгкий tilt-shift: дальний план размыт, игровая площадка — в фокусе
            var dof = profile.Add<DepthOfField>(true);
            dof.mode.Override(DepthOfFieldMode.Gaussian);
            dof.gaussianStart.Override(CameraRig.Distance + 9f);
            dof.gaussianEnd.Override(CameraRig.Distance + 22f);
            dof.gaussianMaxRadius.Override(1.2f);
            vol.profile = profile;
        }

        void Update()
        {
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            input.Poll();

            if (input.DeckPressed && !Sim.GameOver && !Sim.RewardPending) viewSkills = !viewSkills;
            else if (input.PausePressed)
            {
                if (viewSkills) viewSkills = false;
                else if (!Sim.RewardPending) pauseMenu = !pauseMenu;
            }
            else if (viewSkills && input.NavBack) viewSkills = false; // B — закрыть просмотр
            else if (pauseMenu && input.NavBack) pauseMenu = false;   // B — продолжить
            // заново: R; с геймпада — Y в паузе, Menu после смерти
            if (input.RestartPressed || (pauseMenu && input.NavAlt) || (Sim.GameOver && wasGameOver && input.PausePressed)) Restart();
            bool wasPaused = Paused;
            reward.Sync(viewSkills, input);
            Paused = pauseMenu || reward.Visible;
            if (wasPaused && !Paused) input.Flush(); // клик по карточке не должен стать выстрелом

            if (!Paused)
            {
                // стоп-кадр при ударе по Ace (SkillEventHitFreeze 0.15 с при timeScale 0.01)
                float simDt = dt;
                if (slowT >= 0f) { slowT += dt; simDt *= SimSlowAt(slowSteps, slowT); if (slowT >= slowTotal) slowT = -1f; }
                time += simDt;
                acc += simDt;
                bool first = true;
                int guard = 0;
                watch.Restart();
                using (SimMarker.Auto())
                    while (acc >= Step && guard++ < 6)
                    {
                        acc -= Step;
                        Sim.Step(Step, TestInput?.Invoke() ?? input.Build(Rig.Cam, first));
                        first = false;
                        HandleEvents();
                    }
                SimMs = (float)watch.Elapsed.TotalMilliseconds;
                if (Sim.GameOver && !wasGameOver) hud.ShowBanner(input.UseMouse ? "Смерть повержена — R, чтобы начать заново" : "Смерть повержена — Menu, чтобы начать заново", 999f);
                wasGameOver = Sim.GameOver;
            }

            UpdateCameraFeel(Paused ? 0f : dt);
            watch.Restart();
            using (ViewsMarker.Auto()) SyncViews(Paused ? 0f : dt);
            ViewsMs = (float)watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            using (HudMarker.Auto()) hud.Sync(Sim, Rig.Cam, input.UseMouse, dt, Paused);
            HudMs = (float)watch.Elapsed.TotalMilliseconds;
            if (pauseMenu) hud.ShowBanner(input.UseMouse ? "Пауза · Esc — продолжить · R — заново" : "Пауза · Menu или B — продолжить · Y — заново", 0.1f);
        }

        void Restart()
        {
            Sim.Restart();
            foreach (var v in enemyViews.Values) v.Destroy();
            enemyViews.Clear();
            effects.Clear();
            hud.ShowBanner("", 0f);
            Paused = false; wasGameOver = false;
            viewSkills = false; pauseMenu = false;
        }

        void SyncViews(float dt)
        {
            player.Sync(Sim, dt, time);
            foreach (var e in Sim.Enemies)
            {
                if (!enemyViews.TryGetValue(e.Id, out var v)) enemyViews[e.Id] = v = new EnemyView(e, world);
                v.Sync(dt, time);
            }
            stale.Clear();
            foreach (var kv in enemyViews) if (!Sim.Enemies.Contains(kv.Value.E)) stale.Add(kv.Key);
            foreach (var id in stale) { enemyViews[id].Destroy(); enemyViews.Remove(id); }
            effects.Sync(Sim, dt, time);

            var l = Sim.Player.Lock;
            lockMarker.gameObject.SetActive(l != null && l.Alive);
            if (l != null)
            {
                lockMarker.position = Art.W(l.Pos, Art.FloorTop + 0.06f);
                lockMarker.localScale = Vector3.one * (l.R * 2.8f + Mathf.Sin(time * 8f) * 0.08f);
            }
            Rig.Follow(Art.W(Sim.Player.Pos), dt);
        }

        void HandleEvents()
        {
            foreach (var ev in Sim.Events)
            {
                switch (ev.Kind)
                {
                    case EventKind.EnemyHit:
                        // тики Scorch — без цифр, как в Hades (BurnEffect: SkipDamageText); горение видно по цвету врага
                        if (Sim.FormsMode && ev.Color == 10 + (int)DotKind.Burn) break;
                        hud.Number(ev.Pos, ev.Value, ev.Crit, NumberColor(ev));
                        break;
                    case EventKind.EnemyDied:
                        effects.Puff(ev.Pos, 1.2f, new Color(0.9f, 0.85f, 1f), 0.4f);
                        break;
                    case EventKind.PlayerHit:
                        // хитстоп и тряска от вражеского удара (HitSimSlowParameters / HitScreenshake снаряда врага)
                        if (ev.Color >= 0) { StartSlow(Sim.PlayerCfg.hitSlow[ev.Color], false); Rig.Shake(Sim.PlayerCfg.hitShake, Sim.PlayerCfg.hitShakeTime[ev.Color]); }
                        hud.HurtFlash();
                        effects.Puff(ev.Pos, 0.9f, new Color(1f, 0.3f, 0.3f), 0.2f);
                        break;
                    case EventKind.HitStop:
                        StartSlow(Sim.StaffCfg.hitSlow[(int)ev.Value], ev.Crit); // на убийстве второй шаг ждёт +0.04 с
                        Rig.Shake(ev.Radius, ev.To.x);
                        break;
                    case EventKind.Shake:
                        pendingShakes.Add(new Vector3(ev.To.x, ev.Value, ev.Radius));
                        break;
                    case EventKind.PlayerBlocked:
                        effects.Ring(ev.Pos, 1f, new Color(1f, 0.9f, 0.4f, 0.9f));
                        break;
                    case EventKind.Explosion:
                        effects.Ring(ev.Pos, ev.Radius, ExplosionColor(ev.Color));
                        break;
                    case EventKind.Attack:
                        if (ev.Radius > 0f) effects.Ring(ev.Pos, ev.Radius * 0.8f, new Color(1f, 0.35f, 0.2f, 0.9f), 0.25f);
                        break;
                    case EventKind.Teleport:
                        effects.Puff(ev.Pos, 1f, ExplosionColor(ev.Color), 0.3f);
                        effects.Puff(ev.To, 1f, ExplosionColor(ev.Color), 0.3f);
                        break;
                    case EventKind.Zap:
                        effects.Zap(ev.Pos, ev.To, ev.Color == 1 ? new Color(1f, 0.8f, 0.45f, 0.9f) : new Color(1f, 0.97f, 0.6f, 1f));
                        break;
                    case EventKind.WaveStart:
                        hud.ShowBanner($"Волна {Mathf.RoundToInt(ev.Value)}");
                        break;
                }
            }
        }

        void StartSlow(float[] steps, bool kill)
        {
            if (slowT >= 0f) return;
            slowSteps = (float[])steps.Clone();
            if (kill && slowSteps.Length >= 6) slowSteps[3] += 0.04f;
            slowTotal = 0f;
            for (int i = 0; i + 2 < slowSteps.Length; i += 3) slowTotal += slowSteps[i] + slowSteps[i + 2];
            slowT = 0f;
        }

        /// <summary>Тряска с задержкой (ScreenPreWait) и приближение камеры на Ω-заряде (ChargeCameraMotion).</summary>
        void UpdateCameraFeel(float dt)
        {
            for (int i = pendingShakes.Count - 1; i >= 0; i--)
            {
                var sh = pendingShakes[i];
                sh.x -= dt;
                if (sh.x <= 0f) { Rig.Shake(sh.y, sh.z); pendingShakes.RemoveAt(i); }
                else pendingShakes[i] = sh;
            }
            var c = Sim.StaffCfg;
            bool charging = Sim.Player.Staff.Move == StaffMove.OmegaAttack;
            zoomK = Mathf.MoveTowards(zoomK, charging ? 1f : 0f, dt / (charging ? c.omegaZoomIn : c.omegaZoomOut));
            Rig.ZoomMul = Mathf.Lerp(1f, c.omegaZoom, zoomK);
        }

        /// <summary>Скорость симуляции через t реальных секунд после попадания: шаги {ждать, доля, переход}.</summary>
        static float SimSlowAt(float[] steps, float t)
        {
            float cur = 1f, at = 0f;
            for (int i = 0; i + 2 < steps.Length; i += 3)
            {
                at += steps[i];
                if (t < at) return cur;
                float lerp = steps[i + 2];
                if (lerp > 0f && t < at + lerp) return Mathf.Lerp(cur, steps[i + 1], (t - at) / lerp);
                cur = steps[i + 1]; at += lerp;
            }
            return cur;
        }

        static Color NumberColor(SimEvent ev)
        {
            if (ev.Crit) return new Color(1f, 0.88f, 0.3f);
            return ev.Color switch
            {
                10 => new Color(1f, 0.35f, 0.4f),   // Bleed
                11 => new Color(1f, 0.65f, 0.3f),   // Burn
                12 => new Color(0.55f, 0.95f, 0.4f), // Poison
                13 => new Color(0.85f, 0.5f, 1f),   // Hex
                14 => new Color(0.45f, 0.8f, 1f),   // Lava (синий огонь)
                15 => new Color(0.55f, 0.95f, 0.4f),
                16 => new Color(1f, 0.95f, 0.6f),
                _ => Color.white,
            };
        }

        static Color ExplosionColor(int c) => c switch
        {
            0 => new Color(1f, 0.3f, 0.5f, 0.9f),
            1 => new Color(1f, 0.9f, 0.35f, 0.9f),
            2 => new Color(0.35f, 0.9f, 1f, 0.9f),
            3 => new Color(1f, 0.6f, 0.2f, 0.9f),
            4 => new Color(0.5f, 0.95f, 0.35f, 0.9f),
            _ => new Color(0.75f, 0.45f, 1f, 0.9f),
        };
    }
}
