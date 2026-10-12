using Arena.Sim;
using UnityEngine;

namespace Arena.View
{
    /// <summary>Ace-фокусник: плащ, голова, цилиндр. Под ним — стрелка прицела (направление следующего удара).</summary>
    public sealed class PlayerView
    {
        public readonly Transform Root;
        readonly Transform body, model, aim, shield, reticle, aimLine;
        readonly Renderer[] renderers;
        readonly Material aimMat, reticleMat, lineMat;
        float bob;

        public PlayerView(Transform parent)
        {
            Root = new GameObject("Ace").transform;
            Root.SetParent(parent, false);
            model = new GameObject("Model").transform;
            model.SetParent(Root, false);
            body = new GameObject("Body").transform;
            body.SetParent(model, false);
            var cape = Art.LitMat(new Color(0.36f, 0.18f, 0.62f));
            var skin = Art.LitMat(new Color(0.98f, 0.85f, 0.76f));
            var hat = Art.LitMat(new Color(0.16f, 0.09f, 0.22f));
            var gold = Art.LitMat(new Color(0.95f, 0.78f, 0.32f));
            Art.Part(body, MeshKit.Prism(7, 0.45f), cape, Vector3.zero, new Vector3(0.75f, 0.85f, 0.7f));
            Art.Part(body, MeshKit.Prism(7, 0.9f), Art.LitMat(new Color(0.95f, 0.93f, 0.9f)), new Vector3(0f, 0.55f, 0.06f), new Vector3(0.36f, 0.3f, 0.3f));
            Art.Part(body, MeshKit.Ico(1), skin, new Vector3(0f, 1.05f, 0f), Vector3.one * 0.48f);
            Art.Part(body, MeshKit.Ico(0), skin, new Vector3(0f, 1.0f, 0.24f), Vector3.one * 0.1f); // нос — показывает взгляд
            Art.Part(body, MeshKit.Prism(8), hat, new Vector3(0f, 1.24f, 0f), new Vector3(0.62f, 0.05f, 0.62f));
            Art.Part(body, MeshKit.Prism(8), hat, new Vector3(0f, 1.27f, 0f), new Vector3(0.36f, 0.42f, 0.36f));
            Art.Part(body, MeshKit.Prism(8), Art.LitMat(Art.Pink), new Vector3(0f, 1.32f, 0f), new Vector3(0.38f, 0.07f, 0.38f));
            Art.Part(body, MeshKit.Prism(5), gold, new Vector3(0.28f, 0.2f, 0.18f), new Vector3(0.06f, 0.6f, 0.06f), new Vector3(10f, 0f, -12f)); // трость
            renderers = Root.GetComponentsInChildren<Renderer>();

            aimMat = Art.DecalInstance(new Color(1f, 1f, 1f, 0.55f), -1, 5);
            aim = Art.Decal(Root, MeshKit.Wedge(), aimMat, 0.04f).transform;
            aim.localScale = new Vector3(0.35f, 0.2f, 0.45f);

            // прицел жёлтых карт (GunGrenadeToss) и линия прицела (ShowFreeAimLine) — в мире, не в Ace
            reticleMat = Art.DecalInstance(new Color(1f, 0.9f, 0.3f, 0.9f), 1, 8);
            reticle = Art.Decal(parent, MeshKit.FloorQuad(), reticleMat, 0.07f).transform;
            lineMat = Art.DecalInstance(new Color(1f, 0.9f, 0.4f, 0.35f), -1, 6);
            aimLine = Art.Decal(parent, MeshKit.FloorLane(), lineMat, 0.05f).transform;
            reticle.gameObject.SetActive(false);
            aimLine.gameObject.SetActive(false);

            var shieldGo = Art.Part(Root, MeshKit.Ico(1), Art.LitMat(new Color(1f, 0.9f, 0.3f, 0.3f), 1.2f, true), new Vector3(0f, 0.7f, 0f), Vector3.one * 1.8f, default, false);
            shield = shieldGo.transform;
            shieldGo.SetActive(false);
        }

        public void Sync(ArenaSim sim, float dt, float time)
        {
            var p = sim.Player;
            Root.position = Art.W(p.Pos);
            bool hidden = p.Hidden > 0f;
            model.gameObject.SetActive(!hidden);
            var face = p.Face.sqrMagnitude > 0.0001f ? p.Face : Vector2.up;
            model.rotation = Quaternion.Slerp(model.rotation, Quaternion.LookRotation(new Vector3(face.x, 0f, face.y)), Mathf.Clamp01(dt * 30f));
            bob += dt * (p.Moving ? 14f : 3f);
            float squash = p.Cast != null && p.Cast.Phase is CastPhase.Aim or CastPhase.Casting or CastPhase.Hold ? 0.08f : 0f;
            if (p.Cast != null && p.Cast.Phase == CastPhase.Hold) squash = 0.06f + 0.1f * Mathf.Sin(time * 30f) * 0.3f;
            body.localPosition = new Vector3(0f, p.Moving ? Mathf.Abs(Mathf.Sin(bob)) * 0.08f : 0f, 0f);
            body.localScale = new Vector3(1f + squash, 1f - squash, 1f + squash);
            body.localEulerAngles = new Vector3(p.Moving ? 8f : 0f, 0f, 0f);
            // мигание неуязвимости
            bool blink = (p.HitInvuln > 0f || p.BuffInvuln > 0f) && Mathf.Repeat(time * 20f, 1f) < 0.5f;
            foreach (var r in renderers) r.enabled = !blink;
            var a = p.Aim.sqrMagnitude > 0.0001f ? p.Aim : face;
            aim.gameObject.SetActive(!hidden);
            aim.localPosition = new Vector3(a.x * 0.85f, 0.04f, a.y * 0.85f);
            aim.rotation = Quaternion.LookRotation(new Vector3(a.x, 0f, a.y));
            aimMat.SetColor("_BaseColor", p.Cast != null ? new Color(1f, 0.95f, 0.5f, 0.9f) : new Color(1f, 1f, 1f, 0.55f));
            shield.gameObject.SetActive(p.Shield > 0f);
            shield.localScale = Vector3.one * (1.8f + Mathf.Sin(time * 12f) * 0.05f);

            bool aiming = p.Cast != null && (p.Cast.Phase == CastPhase.Aim || (p.Cast.Phase == CastPhase.Casting && sim.Slots[p.Cast.Slot].Card?.Color == Arena.Core.CardColor.Yellow));
            reticle.gameObject.SetActive(aiming && !hidden);
            aimLine.gameObject.SetActive(aiming && !hidden);
            if (aiming)
            {
                var target = p.Cast.Target;
                reticle.position = Art.W(target, 0.07f);
                reticle.localScale = Vector3.one * (0.9f + Mathf.Sin(time * 10f) * 0.06f);
                var d = target - p.Pos;
                aimLine.position = Art.W(p.Pos, 0.05f);
                aimLine.rotation = Quaternion.LookRotation(new Vector3(d.x, 0f, d.y).sqrMagnitude > 0.0001f ? new Vector3(d.x, 0f, d.y) : Vector3.forward);
                aimLine.localScale = new Vector3(0.12f, 1f, d.magnitude);
            }
        }
    }

    /// <summary>Враг: призрак или топотун, полоска здоровья, иконки статусов, «!» и обозначение атаки.</summary>
    public sealed class EnemyView
    {
        public readonly Enemy E;
        public readonly Transform Root;
        readonly Transform model, body, hpFill, alert, statusRoot, legL, legR;
        readonly Renderer[] renderers;
        readonly Material[] flashMats;
        readonly Color[] baseColors;
        readonly TelegraphView tele;
        readonly GameObject spawnDecal;
        readonly Material spawnMat;
        readonly Transform[] statusIcons = new Transform[7];
        float t;

        public EnemyView(Enemy e, Transform parent)
        {
            E = e;
            Root = new GameObject(e.Kind.ToString()).transform;
            Root.SetParent(parent, false);
            model = new GameObject("Model").transform;
            model.SetParent(Root, false);
            body = new GameObject("Body").transform;
            body.SetParent(model, false);
            switch (e.Kind)
            {
                case EnemyKind.Ghost: BuildGhost(); break;
                case EnemyKind.Stomper: BuildStomper(out legL, out legR); break;
                case EnemyKind.Thug: BuildThug(out legL, out legR); break;
                default: BuildWitch(); break;
            }
            renderers = body.GetComponentsInChildren<Renderer>();
            flashMats = new Material[renderers.Length];
            baseColors = new Color[renderers.Length];
            for (int i = 0; i < renderers.Length; i++)
            {
                flashMats[i] = renderers[i].material; // свой экземпляр — для вспышек
                baseColors[i] = flashMats[i].GetColor("_BaseColor");
            }

            // полоска здоровья — смотрит в камеру
            var bar = new GameObject("Hp").transform;
            bar.SetParent(Root, false);
            bar.localPosition = new Vector3(0f, Height(e.Kind) + 0.35f, 0f);
            var bg = Art.Part(bar, MeshKit.Box(), Art.DecalMat(new Color(0.1f, 0.05f, 0.12f, 0.8f), -1, 20), Vector3.zero, new Vector3(0.9f, 0.1f, 0.01f), default, false);
            hpFill = Art.Part(bar, MeshKit.Box(), Art.DecalMat(new Color(1f, 0.3f, 0.4f, 1f), -1, 21), new Vector3(0f, 0f, -0.01f), new Vector3(0.86f, 0.07f, 0.01f), default, false).transform;
            bar.gameObject.AddComponent<Billboard>();
            statusRoot = new GameObject("Status").transform;
            statusRoot.SetParent(bar, false);
            statusRoot.localPosition = new Vector3(-0.4f, 0.18f, 0f);
            Color[] sc = { new(1f, 0.9f, 0.2f), new(0.7f, 0.42f, 1f), new(0.45f, 0.65f, 1f), new(1f, 0.19f, 0.28f), new(1f, 0.6f, 0.12f), new(0.48f, 0.88f, 0.29f), new(0.85f, 0.4f, 1f) };
            for (int i = 0; i < statusIcons.Length; i++)
            {
                statusIcons[i] = Art.Part(statusRoot, MeshKit.Ico(0), Art.DecalMat(sc[i], -1, 22), Vector3.zero, Vector3.one * 0.13f, default, false).transform;
                statusIcons[i].gameObject.SetActive(false);
            }

            // «!» — заметил Ace
            alert = new GameObject("Alert").transform;
            alert.SetParent(Root, false);
            alert.localPosition = new Vector3(0f, Height(e.Kind) + 0.75f, 0f);
            var am = Art.LitMat(new Color(1f, 0.85f, 0.2f), 1.5f);
            Art.Part(alert, MeshKit.Box(), am, new Vector3(0f, 0.18f, 0f), new Vector3(0.1f, 0.3f, 0.1f), default, false);
            Art.Part(alert, MeshKit.Box(), am, new Vector3(0f, -0.06f, 0f), new Vector3(0.1f, 0.1f, 0.1f), default, false);
            alert.gameObject.SetActive(false);

            spawnMat = Art.DecalInstance(new Color(0.75f, 0.35f, 1f, 0.6f), 1, 1);
            spawnDecal = Art.Decal(Root, MeshKit.FloorQuad(), spawnMat, 0.03f);
            tele = new TelegraphView(parent);
        }

        static float Height(EnemyKind k) => k switch { EnemyKind.Ghost => 1.55f, EnemyKind.Thug => 1.9f, EnemyKind.Witch => 1.45f, _ => 0.9f };

        /// <summary>Wretched Thug: громила-тень с дубиной, выше Ace (Tallness 380 против 200).</summary>
        void BuildThug(out Transform l, out Transform r)
        {
            var shade = Art.LitMat(new Color(0.42f, 0.36f, 0.5f));
            var dark = Art.LitMat(new Color(0.22f, 0.18f, 0.28f));
            var eye = Art.LitMat(new Color(1f, 0.85f, 0.35f), 2f);
            var wood = Art.LitMat(new Color(0.5f, 0.36f, 0.26f));
            Art.Part(body, MeshKit.Ico(1, 0.08f, 11), shade, new Vector3(0f, 1.05f, 0f), new Vector3(1.15f, 1.1f, 0.95f));
            Art.Part(body, MeshKit.Ico(1, 0.06f, 12), shade, new Vector3(0f, 1.75f, 0.15f), new Vector3(0.55f, 0.5f, 0.5f));
            Art.Part(body, MeshKit.Ico(0), eye, new Vector3(-0.12f, 1.8f, 0.38f), Vector3.one * 0.09f);
            Art.Part(body, MeshKit.Ico(0), eye, new Vector3(0.12f, 1.8f, 0.38f), Vector3.one * 0.09f);
            Art.Part(body, MeshKit.Ico(1, 0.05f, 13), shade, new Vector3(-0.62f, 1.15f, 0.1f), new Vector3(0.35f, 0.6f, 0.35f));
            // правая рука с дубиной — ей и бьёт трапецией перед собой
            Art.Part(body, MeshKit.Ico(1, 0.05f, 14), shade, new Vector3(0.6f, 1.1f, 0.2f), new Vector3(0.35f, 0.55f, 0.35f));
            Art.Part(body, MeshKit.Prism(6, 1.3f), wood, new Vector3(0.62f, 0.85f, 0.4f), new Vector3(0.22f, 0.85f, 0.22f), new Vector3(70f, 0f, 0f));
            l = Art.Part(model, MeshKit.Prism(6), dark, new Vector3(-0.28f, 0f, 0f), new Vector3(0.28f, 0.55f, 0.28f)).transform;
            r = Art.Part(model, MeshKit.Prism(6), dark, new Vector3(0.28f, 0f, 0f), new Vector3(0.28f, 0.55f, 0.28f)).transform;
        }

        /// <summary>Wretched Witch: тень в балахоне со светящимся шаром в руке — им и стреляет.</summary>
        void BuildWitch()
        {
            var robe = Art.LitMat(new Color(0.3f, 0.24f, 0.42f));
            var hood = Art.LitMat(new Color(0.2f, 0.15f, 0.3f));
            var glow = Art.LitMat(new Color(0.75f, 0.45f, 1f), 2.5f);
            Art.Part(body, MeshKit.Prism(7, 0.35f), robe, new Vector3(0f, 0f, 0f), new Vector3(0.85f, 1.15f, 0.8f));
            Art.Part(body, MeshKit.Prism(6, 0f), hood, new Vector3(0f, 1.1f, 0f), new Vector3(0.55f, 0.55f, 0.55f));
            Art.Part(body, MeshKit.Ico(0), glow, new Vector3(-0.1f, 1.2f, 0.25f), Vector3.one * 0.07f);
            Art.Part(body, MeshKit.Ico(0), glow, new Vector3(0.1f, 1.2f, 0.25f), Vector3.one * 0.07f);
            Art.Part(body, MeshKit.Ico(1), glow, new Vector3(0.38f, 0.9f, 0.35f), Vector3.one * 0.22f);
        }

        void BuildGhost()
        {
            var pale = Art.LitMat(new Color(0.78f, 0.88f, 0.98f), 0.25f);
            var dark = Art.LitMat(new Color(0.2f, 0.16f, 0.3f));
            var eye = Art.LitMat(new Color(1f, 0.2f, 0.15f), 2.5f);
            Art.Part(body, MeshKit.Ico(1, 0.06f, 3), pale, new Vector3(0f, 1.15f, 0f), new Vector3(0.75f, 0.8f, 0.7f));
            Art.Part(body, MeshKit.Prism(7, 0.15f), pale, new Vector3(0f, 0.35f, 0f), new Vector3(0.7f, 0.85f, 0.65f), new Vector3(180f, 0f, 0f));
            Art.Part(body, MeshKit.Ico(0), eye, new Vector3(-0.14f, 1.2f, 0.3f), new Vector3(0.12f, 0.08f, 0.06f));
            Art.Part(body, MeshKit.Ico(0), eye, new Vector3(0.14f, 1.2f, 0.3f), new Vector3(0.12f, 0.08f, 0.06f));
            // когти-коса
            Art.Part(body, MeshKit.Prism(4, 0f), dark, new Vector3(0.45f, 0.9f, 0.25f), new Vector3(0.12f, 0.7f, 0.12f), new Vector3(70f, 0f, -20f));
            Art.Part(body, MeshKit.Prism(4, 0f), dark, new Vector3(-0.45f, 0.9f, 0.25f), new Vector3(0.12f, 0.7f, 0.12f), new Vector3(70f, 0f, 20f));
        }

        void BuildStomper(out Transform l, out Transform r)
        {
            var coral = Art.LitMat(new Color(0.98f, 0.5f, 0.42f));
            var dark = Art.LitMat(new Color(0.45f, 0.2f, 0.2f));
            var white = Art.LitMat(Color.white);
            Art.Part(body, MeshKit.Ico(1, 0.05f, 5), coral, new Vector3(0f, 0.55f, 0f), new Vector3(0.72f, 0.62f, 0.72f));
            Art.Part(body, MeshKit.Ico(0), white, new Vector3(-0.15f, 0.65f, 0.28f), Vector3.one * 0.16f);
            Art.Part(body, MeshKit.Ico(0), white, new Vector3(0.15f, 0.65f, 0.28f), Vector3.one * 0.16f);
            Art.Part(body, MeshKit.Ico(0), dark, new Vector3(-0.15f, 0.65f, 0.35f), Vector3.one * 0.07f);
            Art.Part(body, MeshKit.Ico(0), dark, new Vector3(0.15f, 0.65f, 0.35f), Vector3.one * 0.07f);
            l = Art.Part(model, MeshKit.Prism(5), dark, new Vector3(-0.17f, 0f, 0f), new Vector3(0.12f, 0.3f, 0.12f)).transform;
            r = Art.Part(model, MeshKit.Prism(5), dark, new Vector3(0.17f, 0f, 0f), new Vector3(0.12f, 0.3f, 0.12f)).transform;
        }

        public void Sync(float dt, float time)
        {
            var e = E;
            t += dt;
            Root.position = Art.W(e.Pos);
            bool spawning = e.State == AiState.Spawn;
            // проявление: последние 1.9 с руны враг тёмным силуэтом проступает из пола
            bool fading = spawning && e.HadesKind && e.T < 1.9f;
            model.gameObject.SetActive(!spawning || fading);
            if (fading) model.localPosition = new Vector3(0f, -1.2f * Mathf.Clamp01(e.T / 1.9f), 0f);
            else model.localPosition = Vector3.zero;
            spawnDecal.SetActive(spawning);
            if (spawning)
            {
                // руна призыва Hades (EnemySummonRune) 2 с, размер по врагу: 0.52 / 0.38 / 0.26
                float k = Mathf.Clamp01(1f - e.T / 2f);
                float rune = e.Kind == EnemyKind.Witch ? 1.5f : 2f;
                spawnDecal.transform.localScale = Vector3.one * (rune * (0.5f + 0.5f * k));
                spawnMat.SetColor("_BaseColor", new Color(0.75f, 0.35f, 1f, 0.3f + 0.5f * k));
            }
            var face = e.Face.sqrMagnitude > 0.0001f ? e.Face : Vector2.down;
            model.rotation = Quaternion.Slerp(model.rotation, Quaternion.LookRotation(new Vector3(face.x, 0f, face.y)), Mathf.Clamp01(dt * 12f));

            bool windup = e.State == AiState.Windup;
            float scale = 1f;
            if (windup && e.Tele != null) scale = 1f + 0.3f * e.Tele.Progress; // AttackIncreaseScale 1.3 за время замаха
            if (e.Dead) scale = Mathf.Clamp01(e.DeathT / 0.8f);
            if (e.HadesKind && windup) scale = 1f; // в Hades враг не растёт — только белеет и дрожит
            float hover = e.Kind is EnemyKind.Ghost or EnemyKind.Witch ? Mathf.Sin(t * 3f) * 0.08f : 0f;
            float hop = e.Kind == EnemyKind.Stomper && (e.State is AiState.Chase or AiState.Wander) && e.Moved > 0.001f ? Mathf.Abs(Mathf.Sin(t * 16f)) * 0.08f : 0f;
            // дрожь: от удара (6 ед. Hades, 0.15 с) и в конце замаха (3 ед.)
            var jitter = Vector3.zero;
            if (e.Shake > 0f) jitter += new Vector3(Mathf.Sin(t * 90f), 0f, Mathf.Cos(t * 77f)) * Arena.Core.Hades.U(6f) * 2f;
            if (e.WarnT > 0f) jitter += new Vector3(Mathf.Sin(t * 110f), 0f, Mathf.Cos(t * 95f)) * Arena.Core.Hades.U(3f) * 3f;
            body.localPosition = new Vector3(0f, hover + hop, 0f) + jitter;
            body.localScale = Vector3.one * scale;
            if (e.State == AiState.Strike || e.State == AiState.Lunge) body.localEulerAngles = new Vector3(20f, 0f, 0f);
            else if (e.HadesKind && (e.State is AiState.Windup or AiState.PreFire)) body.localEulerAngles = new Vector3(-12f, 0f, 0f); // отклоняется назад в замахе
            else if (e.Stun > 0f) body.localEulerAngles = new Vector3(0f, 0f, Mathf.Sin(t * 20f) * 8f);
            else body.localEulerAngles = Vector3.zero;
            if (legL != null)
            {
                float s = e.Moved > 0.001f ? Mathf.Sin(t * (e.Kind == EnemyKind.Thug ? 9f : 16f)) * 0.12f : 0f;
                legL.localPosition = new Vector3(legL.localPosition.x, Mathf.Max(0f, s), 0f);
                legR.localPosition = new Vector3(legR.localPosition.x, Mathf.Max(0f, -s), 0f);
            }

            // вспышки Hades: красная при уроне (0.03 с), белая в конце замаха (0 → 80%); у наших врагов — красная пульсация замаха
            Color tint = Color.clear;
            if (e.Flash > 0f) tint = new Color(0.86f, 0.08f, 0.08f, 1f);
            else if (e.HadesKind && e.WarnT > 0f) tint = new Color(1f, 1f, 1f, 0.8f * e.WarnT);
            else if (windup && !e.HadesKind) tint = Color.Lerp(new Color(1f, 0.3f, 0.2f, 0f), new Color(1f, 0.15f, 0.1f, 0.85f), Mathf.Repeat(time * 8f, 1f) < 0.5f ? 1f : 0.4f);
            for (int i = 0; i < flashMats.Length; i++)
                flashMats[i].SetColor("_BaseColor", Color.Lerp(baseColors[i], new Color(tint.r, tint.g, tint.b, 1f), tint.a));

            hpFill.localScale = new Vector3(0.86f * Mathf.Clamp01(e.Hp / e.MaxHp), 0.07f, 0.01f);
            hpFill.localPosition = new Vector3(-0.43f * (1f - Mathf.Clamp01(e.Hp / e.MaxHp)), 0f, -0.01f);
            hpFill.parent.gameObject.SetActive(!spawning && !e.Dead);
            alert.gameObject.SetActive(e.Alert > 0f && !e.Dead);
            if (e.Alert > 0f) alert.localScale = Vector3.one * (1f + Mathf.Max(0f, e.Alert - 0.6f) * 2f);

            bool[] on =
            {
                e.Stun > 0f, e.Fear > 0f, e.Pull > 0f || e.PullAce > 0f,
                HasDot(e, DotKind.Bleed) || e.MarkBleed > 0f, HasDot(e, DotKind.Burn) || HasDot(e, DotKind.Lava),
                HasDot(e, DotKind.Poison) || HasDot(e, DotKind.Noxious) || e.MarkPoison > 0f, HasDot(e, DotKind.Hex),
            };
            int n = 0;
            for (int i = 0; i < on.Length; i++)
            {
                statusIcons[i].gameObject.SetActive(on[i] && !e.Dead);
                if (on[i]) statusIcons[i].localPosition = new Vector3(0.16f * n++, 0f, 0f);
            }
            tele.Sync(e.Dead ? null : e.Tele, time);
        }

        static bool HasDot(Enemy e, DotKind k)
        {
            foreach (var d in e.Dots) if (d.Kind == k) return true;
            return false;
        }

        public void Destroy()
        {
            tele.Destroy();
            Object.Destroy(Root.gameObject);
        }
    }

    /// <summary>Обозначение атаки на земле: контур зоны сразу целиком + заливка, которая растёт до контура к моменту удара.</summary>
    public sealed class TelegraphView
    {
        readonly Transform root, outline, fill, edge;
        readonly Material outlineMat, fillMat, edgeMat;
        TeleShape shape = (TeleShape)(-1);

        public TelegraphView(Transform parent)
        {
            root = new GameObject("Telegraph").transform;
            root.SetParent(parent, false);
            outlineMat = Art.DecalInstance(Art.DangerEdge, 1, 2);
            fillMat = Art.DecalInstance(Art.Danger, 0, 3);
            edgeMat = Art.DecalInstance(new Color(1f, 0.25f, 0.15f, 0.2f), 0, 1);
            edge = Art.Decal(root, MeshKit.FloorQuad(), edgeMat, 0.025f).transform;
            outline = Art.Decal(root, MeshKit.FloorQuad(), outlineMat, 0.03f).transform;
            fill = Art.Decal(root, MeshKit.FloorQuad(), fillMat, 0.035f).transform;
            root.gameObject.SetActive(false);
        }

        public void Sync(Telegraph t, float time)
        {
            root.gameObject.SetActive(t != null);
            if (t == null) return;
            if (shape != t.Shape)
            {
                shape = t.Shape;
                bool lane = shape == TeleShape.Lane;
                var quad = lane ? MeshKit.FloorLane() : MeshKit.FloorQuad();
                outline.GetComponent<MeshFilter>().sharedMesh = quad;
                fill.GetComponent<MeshFilter>().sharedMesh = quad;
                edge.GetComponent<MeshFilter>().sharedMesh = quad;
                outlineMat.SetTexture("_BaseMap", lane ? Art.Rect() : Art.Ring());
                fillMat.SetTexture("_BaseMap", lane ? null : Art.Disc());
                edgeMat.SetTexture("_BaseMap", lane ? null : Art.Disc());
            }
            float k = t.Progress;
            root.position = Art.W(t.Center);
            root.rotation = Quaternion.LookRotation(new Vector3(t.Dir.x, 0f, t.Dir.y));
            if (t.Shape == TeleShape.Ellipse)
            {
                var full = new Vector3(t.Side * 2f, 1f, t.Forward * 2f);
                outline.localScale = full;
                edge.localScale = full;
                fill.localScale = new Vector3(full.x * k, 1f, full.z * k);
            }
            else
            {
                outline.localScale = new Vector3(t.Side * 2f, 1f, t.Forward);
                edge.localScale = outline.localScale;
                fill.localScale = new Vector3(t.Side * 2f, 1f, t.Forward * k);
            }
            // к концу замаха — ярче и мигает, чтобы не пропустить момент удара
            float pulse = k > 0.75f ? (Mathf.Repeat(time * 14f, 1f) < 0.5f ? 1f : 0.65f) : 1f;
            outlineMat.SetColor("_BaseColor", new Color(1f, 0.6f, 0.15f, (0.75f + 0.25f * k) * pulse));
            fillMat.SetColor("_BaseColor", new Color(1f, 0.15f, 0.08f, (0.45f + 0.4f * k) * pulse));
            edgeMat.SetColor("_BaseColor", new Color(1f, 0.35f, 0.15f, 0.3f));
        }

        public void Destroy() => Object.Destroy(root.gameObject);
    }

    /// <summary>Поворачивает объект к камере (полоски здоровья).</summary>
    public sealed class Billboard : MonoBehaviour
    {
        void LateUpdate()
        {
            var cam = Camera.main;
            if (cam != null) transform.rotation = cam.transform.rotation;
        }
    }
}
