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
        /// <summary>Тесты и проверка билда: начать сразу с боя, без стартового экрана.</summary>
        public static bool SkipTitle;
        /// <summary>Забег по этажам Enter the Gungeon (ArenaSim.Floor, FloorView, мини-карта). Выключен: по ощущениям не подошло,
        /// забег снова по комнатам Hades; код оставлен на будущее, тест прохода этажа включает его сам.</summary>
        public static bool EtgFloors;
        /// <summary>Забег по этажам из арен Hades: схема Enter the Gungeon, арены Тартара, мосты над бездной (ArenaSim.Floor,
        /// ArenaFloorBuilder). Включён по умолчанию; тесты отдельных комнат его выключают. EtgFloors важнее.</summary>
        public static bool ArenaFloors = true;
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
        LootView loot;
        Hud hud;
        RewardScreen reward; // выбор карточки и просмотр скиллов; прежняя колода (DeckScreen) из интерфейса убрана
        TrinketScreen trinkets; // выбор тринкета с пятачка мини-босса
        RunEndScreen runEnd;    // итоги забега → хаб
        HubView hubView;        // врата и персонажи хаба
        CityView cityView;      // полис: кварталы, залы, прозрачность заслоняющего
        bool startRunPending, toHubPending; // врата / «В хаб»: перестроить мир после шага симуляции
        StartScreen title;
        PauseScreen pause;
        bool viewSkills, pauseMenu;
        float runTime; // время забега без пауз — для сводки в паузе
        readonly HitStopPlayer hitStop = new(); // стоп-кадры Hades по виду удара (HitStop события)
        TrapViews traps;
        FloorView floorView;
        RoomProps props;
        Volume volume;
        Transform world, lockMarker, roomRoot, trapRoot;
        float acc, time;
        // хитстоп (HitSimSlowParameters): шаги профиля, время с начала, длительность; новый не начинается, пока идёт прежний
        float[] slowSteps; float slowT = -1f, slowTotal;
        readonly List<Vector3> pendingShakes = new(); // (через сколько, Distance, Duration) — ScreenPreWait тряски
        float zoomK; // 0…1 — приближение Ω-заряда
        bool wasGameOver;

        void Awake()
        {
            Application.targetFrameRate = 120;
            // подключённые комнаты — Resources/Rooms/index.json (enabled): первая — старт, дверь ведёт в следующую
            Sim = new ArenaSim(System.Environment.TickCount);
            Sim.EnableForms(); // скиллы — формы со свойствами; посох и карты DandyAce остаются в коде, но не предлагаются
            if (EtgFloors) Sim.StartFloors(System.Environment.TickCount);
            else if (ArenaFloors) Sim.EnterHub(); // игра начинается в хабе, забег — через врата
            world = new GameObject("World").transform;
            SetupCamera();
            BuildRoom();
            player = new PlayerView(world);
            effects = new EffectViews(world);
            loot = new LootView(world);
            var lm = Art.DecalInstance(new Color(1f, 0.95f, 0.6f, 0.85f), 1, 7);
            lockMarker = Art.Decal(world, MeshKit.FloorQuad(), lm, 0.06f).transform;
            hud = new Hud();
            reward = new RewardScreen(Sim);
            trinkets = new TrinketScreen(Sim);
            runEnd = new RunEndScreen(() => toHubPending = true);
            pause = new PauseScreen(
                resume: () => pauseMenu = false,
                skills: () => { pauseMenu = false; viewSkills = true; },
                restart: Restart,
                toTitle: () => { if (ArenaFloors && !EtgFloors) ReturnToHub(); else Restart(); title.Show(); });
            title = new StartScreen(StartRun, Quit);
            Rig.Snap(Art.W(Sim.Player.Pos));
            if (SkipTitle || System.Array.IndexOf(System.Environment.GetCommandLineArgs(), "-arenaShot") >= 0) ShowRoomTitle();
            else title.Show();
        }

        /// <summary>Стартовый экран открыт: бой стоит.</summary>
        public bool TitleVisible => title.Visible;

        /// <summary>Открыть или закрыть паузу (тесты; в игре — Esc или Menu).</summary>
        public void SetPauseMenu(bool on) => pauseMenu = on && !Sim.RewardPending;

        /// <summary>«Новый забег» стартового экрана.</summary>
        public void StartRun()
        {
            title.Hide();
            runTime = 0f;
            ShowRoomTitle();
        }

        static void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        void ShowRoomTitle()
        {
            hud.ShowBanner(Sim.LocationTitle, 2f);
        }

        /// <summary>Комната, ловушки, свет и постобработка — день биома Hades.
        /// Камера — по масштабу картинки Hades для этой комнаты (ZoomFraction).</summary>
        void BuildRoom()
        {
            if (roomRoot != null) Destroy(roomRoot.gameObject);
            if (trapRoot != null) Destroy(trapRoot.gameObject);
            // этаж ETG — клетками (FloorView); этаж из арен — многоугольниками, как комната Hades, от FloorView — решётки и выход
            bool cells = Sim.GridCollision;
            floorView = Sim.FloorMode ? new FloorView(Sim, cells) : null;
            roomRoot = cells ? floorView.Root : RoomView.Build(Sim);
            if (floorView != null && !cells) floorView.Root.SetParent(roomRoot, false);
            hubView = Sim.HubMode ? new HubView(Sim) : null; // уничтожается вместе с roomRoot
            if (hubView != null) hubView.Root.SetParent(roomRoot, false);
            cityView = Sim.Room.biome == PolisDressing.Biome ? new CityView(Sim, RoomView.Fadeables) : null;
            if (cityView != null) cityView.Root.SetParent(roomRoot, false);
            roomRoot.SetParent(world, false);
            trapRoot = new GameObject("Traps").transform;
            trapRoot.SetParent(world, false);
            traps = new TrapViews(Sim, trapRoot);
            props = new RoomProps(Sim, trapRoot);
            Rig.SetZoom(Sim.Room.zoom);
            Rig.Cam.backgroundColor = RoomView.For(Sim.Room.biome).Void;
            SetupLight();
            SetupVolume();
        }

        /// <summary>Загрузить комнату и начать в ней заново (галерея комнат в тестах, отладка).</summary>
        public void LoadRoom(RoomDef room)
        {
            Sim.LoadRoom(room);
            foreach (var v in enemyViews.Values) v.Destroy();
            enemyViews.Clear();
            effects.Clear();
            BuildRoom();
            hitStop.Reset();
            slowT = -1f;
            Rig.Snap(Art.W(Sim.Player.Pos));
        }

        /// <summary>Новая комната за дверью: перестроить комнату и снять виды врагов и эффектов прошлой.</summary>
        void EnterRoomViews()
        {
            foreach (var v in enemyViews.Values) v.Destroy();
            enemyViews.Clear();
            effects.Clear();
            BuildRoom();
            Rig.Snap(Art.W(Sim.Player.Pos));
            ShowRoomTitle();
        }

        /// <summary>Проверка билда без участия человека: -arenaShot &lt;файл.png&gt; — через 6 с боя сохранить
        /// кадр (с интерфейсом) и выйти.</summary>
        System.Collections.IEnumerator Start()
        {
            var args = System.Environment.GetCommandLineArgs();
            string shot = null;
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == "-arenaShot") shot = args[i + 1];
            if (shot == null) yield break;
            yield return new WaitForSecondsRealtime(1.5f);
            yield return new WaitForSecondsRealtime(6f);
            ScreenCapture.CaptureScreenshot(shot);
            yield return new WaitForSecondsRealtime(1f);
            Application.Quit();
        }

        void SetupCamera()
        {
            var cam = Camera.main;
            if (cam == null)
            {
                var go = new GameObject("Main Camera") { tag = "MainCamera" };
                cam = go.AddComponent<Camera>();
            }
            Rig = new CameraRig(cam);
            var data = cam.GetUniversalAdditionalCameraData();
            data.renderPostProcessing = true;
            data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
        }

        static void SetupLight()
        {
            var sun = FindAnyObjectByType<Light>();
            if (sun == null) sun = new GameObject("Sun").AddComponent<Light>();
            sun.type = LightType.Directional;
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

        void SetupVolume()
        {
            if (volume == null)
            {
                volume = new GameObject("PostFX").AddComponent<Volume>();
                volume.isGlobal = true;
            }
            var vol = volume;
            if (vol.profile != null) Destroy(vol.profile);
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            var bloom = profile.Add<Bloom>(true);
            bloom.intensity.Override(0.7f); bloom.threshold.Override(0.95f); bloom.scatter.Override(0.65f);
            var tone = profile.Add<Tonemapping>(true);
            tone.mode.Override(TonemappingMode.Neutral);
            var color = profile.Add<ColorAdjustments>(true);
            color.saturation.Override(12f); color.contrast.Override(6f); color.postExposure.Override(0.15f);
            color.colorFilter.Override(new Color(1f, 0.98f, 0.95f));
            var vig = profile.Add<Vignette>(true);
            vig.intensity.Override(0.22f); vig.smoothness.Override(0.5f);
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

            if (title.Visible) title.Sync(input); // стартовый экран: бой стоит, остальной ввод не принимается
            else
            {
                bool backUsed = pauseMenu && pause.Navigate(input); // меню паузы: пункты, B закрывает «Управление»
                if (input.DeckPressed && !Sim.GameOver && !Sim.RewardPending && !pauseMenu) viewSkills = !viewSkills;
                else if (input.PausePressed)
                {
                    if (viewSkills) viewSkills = false;
                    else if (!Sim.RewardPending) pauseMenu = !pauseMenu;
                }
                else if (viewSkills && input.NavBack) viewSkills = false;          // B — закрыть просмотр
                else if (pauseMenu && input.NavBack && !backUsed) pauseMenu = false; // B — продолжить
                // заново: R; с геймпада — Y в паузе, Menu после смерти (в забеге из арен после смерти — итоги и хаб)
                if (!runEnd.Visible && (input.RestartPressed || (pauseMenu && input.NavAlt) || (Sim.GameOver && wasGameOver && input.PausePressed))) Restart();
                // E / RB: подобрать карточку с пятачка, в хабе — врата или разговор
                if (!Paused && input.InteractPressed) Sim.Interact();
            }
            bool wasPaused = Paused;
            if (!title.Visible) { reward.Sync(viewSkills, input); trinkets.Sync(input); runEnd.Sync(input); }
            if (pauseMenu) pause.Show(); else pause.Hide();
            pause.Sync(Sim, runTime, input);
            bool choosing = reward.Visible || trinkets.Visible || runEnd.Visible;
            hud.SetVisible(!title.Visible && !pauseMenu && !choosing); // как в Hades: в паузе и выборе HUD скрыт
            Paused = title.Visible || pauseMenu || choosing;
            if (wasPaused && !Paused) input.Flush(); // клик по карточке не должен стать выстрелом

            if (!Paused)
            {
                // стоп-кадр Hades: профиль по виду удара (HitStopPlayer) и профиль удара посоха (HitSimSlowParameters)
                hitStop.Tick(dt);
                float simDt = dt * hitStop.Scale;
                if (slowT >= 0f) { slowT += dt; simDt *= SimSlowAt(slowSteps, slowT); if (slowT >= slowTotal) slowT = -1f; }
                time += simDt;
                if (!Sim.GameOver) runTime += dt;
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
                if (Sim.GameOver && !wasGameOver)
                {
                    if (Sim.ArenaFloorsOn || Sim.RunWon) runEnd.Show(Sim, runTime); // забег из арен: итоги, потом хаб
                    else hud.ShowBanner(input.UseMouse ? "Смерть повержена — R, чтобы начать заново" : "Смерть повержена — Menu, чтобы начать заново", 999f);
                }
                wasGameOver = Sim.GameOver;
            }
            // врата и «В хаб» перестраивают мир — после шага симуляции (итоги открыты на паузе)
            if (startRunPending || Sim.RunRequested) StartRunFromHub();
            if (toHubPending) ReturnToHub();

            UpdateCameraFeel(Paused ? 0f : dt);
            watch.Restart();
            using (ViewsMarker.Auto()) SyncViews(Paused ? 0f : dt);
            ViewsMs = (float)watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            using (HudMarker.Auto()) hud.Sync(Sim, Rig.Cam, input.UseMouse, dt, Paused);
            HudMs = (float)watch.Elapsed.TotalMilliseconds;
        }

        /// <summary>Врата хаба: новый забег по этажам из арен.</summary>
        public void StartRunFromHub()
        {
            startRunPending = false;
            Sim.StartRun();
            ResetViews();
            ShowRoomTitle();
        }

        /// <summary>«В хаб» на итогах (и выход в меню из забега): снова хаб, билд — как в начале.</summary>
        public void ReturnToHub()
        {
            toHubPending = false;
            runEnd.Hide();
            Sim.EnterHub();
            ResetViews();
            ShowRoomTitle();
        }

        /// <summary>Итоги забега открыты (тесты).</summary>
        public bool RunEndVisible => runEnd.Visible;

        /// <summary>«В хаб» на итогах (тесты; в игре — A, Enter или клик).</summary>
        public void ConfirmRunEnd() { if (runEnd.Visible) ReturnToHub(); }

        void Restart()
        {
            Sim.Restart();
            ResetViews();
        }

        /// <summary>Мир перестроен в симуляции (заново, хаб, забег): виды врагов и эффектов — с нуля, комната — заново.</summary>
        void ResetViews()
        {
            foreach (var v in enemyViews.Values) v.Destroy();
            enemyViews.Clear();
            effects.Clear();
            BuildRoom();
            hitStop.Reset();
            slowT = -1f;
            hud.ShowBanner("", 0f);
            Paused = false; wasGameOver = false;
            viewSkills = false; pauseMenu = false;
            runTime = 0f;
            Rig.Snap(Art.W(Sim.Player.Pos));
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
            loot.Sync(Sim, time);
            traps.Sync(time);
            props.Sync(time);
            floorView?.Sync(time);
            hubView?.Sync(Sim, time);
            cityView?.Sync(Sim, Rig.Cam, dt);

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
                        hitStop.Play(ev.Stop, ev.Kill);
                        break;
                    case EventKind.EnemyDied:
                        effects.Puff(ev.Pos, 1.2f, new Color(0.9f, 0.85f, 1f), 0.4f);
                        break;
                    case EventKind.PlayerHit:
                        // Hades: стоп-кадр атакующего, дрожь экрана 3 px за 0.12 с, красная вспышка героя, кровавая виньетка
                        hitStop.Play(ev.Stop);
                        Rig.Shake(Sim.PlayerCfg.hitShake, ev.Stop is HitStop.CasketBolt or HitStop.HurtLight ? 0.06f : 0.12f);
                        hud.HurtFlash(ev.Kill);
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
                        if (ev.Enemy != null && ev.Enemy.Kind == EnemyKind.Thug)
                            effects.Shape(Sim.ThugHitShape(ev.Enemy), new Color(1f, 0.2f, 0.12f, 0.6f), 0.18f);
                        else if (ev.Enemy != null && ev.Enemy.Kind == EnemyKind.Witch)
                            effects.Puff(ev.Pos, 0.5f, new Color(0.8f, 0.45f, 1f), 0.15f);
                        else if (ev.Radius > 0f) effects.Ring(ev.Pos, ev.Radius * 0.8f, new Color(1f, 0.35f, 0.2f, 0.9f), 0.25f);
                        break;
                    case EventKind.TrapFire:
                        if (ev.Color == 0) { effects.Puff(ev.Pos, 1.2f, new Color(0.8f, 0.78f, 0.75f), 0.3f, 0.2f); Rig.Shake(0.05f); }
                        break;
                    case EventKind.BoltHit:
                        effects.Puff(ev.Pos, 0.6f, new Color(0.8f, 0.45f, 1f), 0.2f);
                        break;
                    case EventKind.RoomClear:
                        hitStop.Play(HitStop.LastKill); // замедление на последнем убийстве, как в Hades
                        break;
                    case EventKind.ArenaCleared:
                        hud.ShowBanner("Арена пройдена", ev.Value);
                        break;
                    case EventKind.ChargeRelease:
                        // отпущено в окне — усиление: белое кольцо и короткий стоп-кадр
                        effects.Ring(ev.Pos, 1.8f, new Color(1f, 1f, 1f, 0.95f));
                        hitStop.Play(HitStop.Quick);
                        break;
                    case EventKind.HubGate: // врата хаба: забег начнётся после шага (мир перестраивается)
                        startRunPending = true;
                        break;
                    case EventKind.LootDropped: // карточки вырастают из пола
                        foreach (var g in Sim.Loot)
                        {
                            var col = g.Trinket != null ? Art.TrinketColor(g.Trinket.Kind) : Art.CardTint(g.Card);
                            effects.Ring(g.Pos, 1.2f, col.WithAlpha(0.9f), 0.45f);
                            effects.Puff(g.Pos, 0.9f, col, 0.35f, 0.2f);
                        }
                        break;
                    case EventKind.LootBurned: // взята другая карточка — эта сгорает
                        effects.Puff(ev.Pos, 1.1f, new Color(1f, 0.55f, 0.15f), 0.45f, 1.2f);
                        effects.Puff(ev.Pos, 0.7f, new Color(0.25f, 0.2f, 0.25f), 0.7f, 1.4f);
                        effects.Ring(ev.Pos, 0.9f, new Color(1f, 0.5f, 0.15f, 0.8f));
                        break;

                    case EventKind.RoomEnter:
                    case EventKind.FloorEnter:
                        EnterRoomViews();
                        break;
                    case EventKind.FloorRoomLocked:
                        Rig.Shake(Sim.PlayerCfg.hitShake * 0.6f, 0.1f); // двери захлопнулись
                        break;
                    case EventKind.FloorRoomCleared:
                        break; // замедление последнего убийства — по RoomClear
                    case EventKind.PropBroken:
                        effects.Puff(ev.Pos, 0.8f, new Color(0.8f, 0.6f, 0.45f), 0.25f, 0.3f);
                        break;
                    case EventKind.Teleport:
                        effects.Puff(ev.Pos, 1f, ExplosionColor(ev.Color), 0.3f);
                        effects.Puff(ev.To, 1f, ExplosionColor(ev.Color), 0.3f);
                        break;
                    case EventKind.Zap:
                        effects.Zap(ev.Pos, ev.To, ev.Color == 1 ? new Color(1f, 0.8f, 0.45f, 0.9f) : new Color(1f, 0.97f, 0.6f, 1f));
                        break;
                    case EventKind.WaveStart:
                        if (ev.Value > 1f) hud.ShowBanner($"Волна {Mathf.RoundToInt(ev.Value)}");
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
