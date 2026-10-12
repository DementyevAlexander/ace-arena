using System.Collections.Generic;
using Arena.Sim;
using Arena.UI;
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

        readonly InputReader input = new();
        readonly Dictionary<int, EnemyView> enemyViews = new();
        readonly List<int> stale = new();
        readonly HitStopPlayer hitStop = new();
        PlayerView player;
        EffectViews effects;
        TrapViews traps;
        Hud hud;
        DeckScreen deck;
        Transform world, lockMarker, roomRoot, trapRoot;
        float acc, time;
        bool wasGameOver;

        void Awake()
        {
            Application.targetFrameRate = 120;
            Sim = new ArenaSim(System.Environment.TickCount);
            world = new GameObject("World").transform;
            SetupCamera();
            SetupLight();
            SetupVolume();
            BuildRoom();
            player = new PlayerView(world);
            effects = new EffectViews(world);
            var lm = Art.DecalInstance(new Color(1f, 0.95f, 0.6f, 0.85f), 1, 7);
            lockMarker = Art.Decal(world, MeshKit.FloorQuad(), lm, 0.06f).transform;
            hud = new Hud();
            deck = new DeckScreen(Sim);
            Rig.Snap(Art.W(Sim.Player.Pos));
        }

        /// <summary>Комната и ловушки из данных Hades; камера — по масштабу картинки Hades для этой комнаты.</summary>
        void BuildRoom()
        {
            if (roomRoot != null) Destroy(roomRoot.gameObject);
            if (trapRoot != null) Destroy(trapRoot.gameObject);
            roomRoot = RoomView.Build(Sim);
            roomRoot.SetParent(world, false);
            trapRoot = new GameObject("Traps").transform;
            trapRoot.SetParent(world, false);
            traps = new TrapViews(Sim, trapRoot);
            Rig.SetZoom(Sim.Room.zoom);
            Rig.Cam.backgroundColor = RoomView.For(Sim.Room.biome).Void;
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
            var sun = FindFirstObjectByType<Light>();
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
            var go = new GameObject("PostFX");
            var vol = go.AddComponent<Volume>();
            vol.isGlobal = true;
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

            if (input.DeckPressed && !Sim.GameOver) { deck.Show(!deck.Visible); Paused = deck.Visible; }
            else if (input.PausePressed)
            {
                if (deck.Visible) { deck.Show(false); Paused = false; }
                else Paused = !Paused;
            }
            if (input.RestartPressed || (Sim.GameOver && wasGameOver && input.PausePressed)) Restart();

            if (!Paused)
            {
                // стоп-кадр Hades: замедление всей игры после удара (HitSimSlowParameters)
                hitStop.Tick(dt);
                float simDt = dt * hitStop.Scale;
                time += simDt;
                acc += simDt;
                bool first = true;
                int guard = 0;
                while (acc >= Step && guard++ < 6)
                {
                    acc -= Step;
                    Sim.Step(Step, input.Build(Rig.Cam, first));
                    first = false;
                    HandleEvents();
                }
                if (Sim.GameOver && !wasGameOver) hud.ShowBanner("Ace повержен — R, чтобы начать заново", 999f);
                wasGameOver = Sim.GameOver;
            }

            SyncViews(Paused ? 0f : dt);
            hud.Sync(Sim, Rig.Cam, input.UseMouse, dt, Paused);
            if (Paused && !deck.Visible) hud.ShowBanner("Пауза", 0.1f);
        }

        void Restart()
        {
            Sim.Restart();
            BuildRoom();
            hitStop.Reset();
            foreach (var v in enemyViews.Values) v.Destroy();
            enemyViews.Clear();
            effects.Clear();
            hud.ShowBanner("", 0f);
            Paused = false; wasGameOver = false;
            deck.Show(false);
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
            traps.Sync(time);

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
                        hud.Number(ev.Pos, ev.Value, ev.Crit, NumberColor(ev));
                        hitStop.Play(ev.Stop, ev.Kill);
                        break;
                    case EventKind.EnemyDied:
                        effects.Puff(ev.Pos, 1.2f, new Color(0.9f, 0.85f, 1f), 0.4f);
                        Rig.Shake(0.08f);
                        break;
                    case EventKind.PlayerHit:
                        // Hades: стоп-кадр атакующего, дрожь экрана 3 px за 0.12 с, красная вспышка героя, кровавая виньетка
                        hitStop.Play(ev.Stop);
                        Rig.Shake(0.12f);
                        hud.HurtFlash(ev.Kill);
                        effects.Puff(ev.Pos, 0.9f, new Color(1f, 0.3f, 0.3f), 0.2f);
                        break;
                    case EventKind.PlayerBlocked:
                        effects.Ring(ev.Pos, 1f, new Color(1f, 0.9f, 0.4f, 0.9f));
                        break;
                    case EventKind.Explosion:
                        effects.Ring(ev.Pos, ev.Radius, ExplosionColor(ev.Color));
                        break;
                    case EventKind.Attack:
                        if (ev.Enemy != null && ev.Enemy.Kind == EnemyKind.Thug)
                            effects.Shape(Sim.ThugHitShape(ev.Enemy), new Color(1f, 0.85f, 0.75f, 0.55f), 0.18f);
                        else if (ev.Enemy != null && ev.Enemy.Kind == EnemyKind.Witch)
                            effects.Puff(ev.Pos, 0.5f, new Color(0.8f, 0.45f, 1f), 0.15f);
                        else
                        {
                            effects.Ring(ev.Pos, ev.Radius * 0.8f, new Color(1f, 0.35f, 0.2f, 0.9f), 0.25f);
                            Rig.Shake(0.06f);
                        }
                        break;
                    case EventKind.TrapFire:
                        if (ev.Color == 0) { effects.Puff(ev.Pos, 1.2f, new Color(0.8f, 0.78f, 0.75f), 0.3f, 0.2f); Rig.Shake(0.05f); }
                        break;
                    case EventKind.BoltHit:
                        effects.Puff(ev.Pos, 0.6f, new Color(0.8f, 0.45f, 1f), 0.2f);
                        break;
                    case EventKind.RoomClear:
                        hitStop.Play(HitStop.LastKill);
                        hud.ShowBanner("Комната пройдена", 1.4f);
                        break;
                    case EventKind.Teleport:
                        effects.Puff(ev.Pos, 1f, ExplosionColor(ev.Color), 0.3f);
                        effects.Puff(ev.To, 1f, ExplosionColor(ev.Color), 0.3f);
                        break;
                    case EventKind.WaveStart:
                        if (ev.Value > 1f) hud.ShowBanner($"Волна {Mathf.RoundToInt(ev.Value)}");
                        break;
                }
            }
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
