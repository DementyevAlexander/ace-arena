using System.Collections.Generic;
using Arena.Sim;
using UnityEngine;

namespace Arena.View
{
    /// <summary>Смерть: тёмный балахон с капюшоном, бледная маска-череп, светящиеся глаза, коса.
    /// Под ней — стрелка прицела (направление следующего удара).</summary>
    public sealed class PlayerView
    {
        public readonly Transform Root;
        readonly Transform body, model, aim, shield, aimLine, aimLineBack, reticle, reticleLine, chargeRing;
        readonly Material chargeMat;
        readonly Renderer[] renderers;
        readonly Material aimMat, reticleMat, reticleLineMat;
        float bob;

        public PlayerView(Transform parent)
        {
            Root = new GameObject("Death").transform;
            Root.SetParent(parent, false);
            model = new GameObject("Model").transform;
            model.SetParent(Root, false);
            body = new GameObject("Body").transform;
            body.SetParent(model, false);
            model.localScale = Vector3.one * 0.82f; // рост как у Мелинои в Hades (~150 пикселей мира ≈ 1.3 юнита)
            var robe = Art.LitMat(new Color(0.11f, 0.09f, 0.14f));
            var robeEdge = Art.LitMat(new Color(0.24f, 0.10f, 0.16f));
            var bone = Art.LitMat(new Color(0.86f, 0.83f, 0.76f));
            var eyes = Art.LitMat(Art.Soul, 4f);
            var shaft = Art.LitMat(new Color(0.20f, 0.15f, 0.13f));
            var blade = Art.LitMat(new Color(0.72f, 0.78f, 0.82f), 0.35f);
            // балахон: широкий низ, рваный подол, плечи
            Art.Part(body, MeshKit.Prism(9, 0.38f), robe, Vector3.zero, new Vector3(0.82f, 1.05f, 0.78f));
            Art.Part(body, MeshKit.Prism(9, 1.1f, false), robeEdge, new Vector3(0f, 0.02f, 0f), new Vector3(0.86f, 0.12f, 0.82f));
            Art.Part(body, MeshKit.Ico(0, 0.15f, 3), robe, new Vector3(0f, 0.95f, -0.02f), new Vector3(0.62f, 0.32f, 0.5f));
            // капюшон и маска-череп внутри
            Art.Part(body, MeshKit.Ico(1, 0.1f, 5), robe, new Vector3(0f, 1.2f, -0.04f), new Vector3(0.5f, 0.56f, 0.52f));
            Art.Part(body, MeshKit.Prism(6, 0f), robe, new Vector3(0f, 1.38f, -0.12f), new Vector3(0.3f, 0.3f, 0.3f), new Vector3(-35f, 0f, 0f)); // острый верх
            Art.Part(body, MeshKit.Ico(1), bone, new Vector3(0f, 1.15f, 0.12f), new Vector3(0.3f, 0.34f, 0.26f));
            Art.Part(body, MeshKit.Ico(0), eyes, new Vector3(-0.065f, 1.19f, 0.245f), Vector3.one * 0.07f, default, false);
            Art.Part(body, MeshKit.Ico(0), eyes, new Vector3(0.065f, 1.19f, 0.245f), Vector3.one * 0.07f, default, false);
            // костяная кисть и коса
            Art.Part(body, MeshKit.Ico(0), bone, new Vector3(0.3f, 0.62f, 0.2f), Vector3.one * 0.11f);
            Art.Part(body, MeshKit.Prism(5), shaft, new Vector3(0.3f, -0.05f, 0.2f), new Vector3(0.06f, 1.75f, 0.06f), new Vector3(0f, 0f, -6f));
            Art.Part(body, MeshKit.Box(), blade, new Vector3(0.12f, 1.62f, 0.26f), new Vector3(0.62f, 0.07f, 0.03f), new Vector3(0f, 20f, 18f));
            Art.Part(body, MeshKit.Box(), blade, new Vector3(-0.2f, 1.5f, 0.32f), new Vector3(0.22f, 0.05f, 0.03f), new Vector3(0f, 20f, -25f));
            // холодный ореол вокруг героини, как у Мелинои в Hades: Смерть читается на тёмной улице
            var halo = new GameObject("Halo").AddComponent<Light>();
            halo.transform.SetParent(Root, false);
            halo.transform.localPosition = new Vector3(0f, 1.6f, 0f);
            halo.type = LightType.Point; halo.color = new Color(0.55f, 0.9f, 0.88f); halo.range = 4f; halo.intensity = 1.6f; halo.shadows = LightShadows.None;
            renderers = Root.GetComponentsInChildren<Renderer>();

            aimMat = Art.DecalInstance(new Color(1f, 1f, 1f, 0.55f), -1, 5);
            aim = Art.Decal(Root, MeshKit.Wedge(), aimMat, 0.04f).transform;
            aim.localScale = new Vector3(0.35f, 0.2f, 0.45f);
            // линия прицела при зажатии (ShowFreeAimLine у WeaponStaffBall; появляется, когда набран минимальный заряд)
            aimLine = Art.Decal(Root, MeshKit.FloorLane(), Art.DecalInstance(new Color(0.55f, 0.95f, 0.9f, 0.55f), -1, 4), 0.035f).transform;
            aimLine.gameObject.SetActive(false);
            aimLineBack = Art.Decal(Root, MeshKit.FloorLane(), Art.DecalInstance(new Color(0.55f, 0.95f, 0.9f, 0.55f), -1, 4), 0.035f).transform;
            aimLineBack.gameObject.SetActive(false);

            // прицел жёлтых карт (GunGrenadeToss) и линия к нему — в мире, не в герое
            reticleMat = Art.DecalInstance(new Color(1f, 0.9f, 0.3f, 0.9f), 1, 8);
            reticle = Art.Decal(parent, MeshKit.FloorQuad(), reticleMat, 0.07f).transform;
            reticleLineMat = Art.DecalInstance(new Color(1f, 0.9f, 0.4f, 0.35f), -1, 6);
            reticleLine = Art.Decal(parent, MeshKit.FloorLane(), reticleLineMat, 0.05f).transform;
            reticle.gameObject.SetActive(false);
            reticleLine.gameObject.SetActive(false);
            // кольцо заряда под ногами: сжимается к полному заряду, в окне «идеального» выпуска — белое
            chargeMat = Art.DecalInstance(new Color(1f, 0.85f, 0.45f, 0.8f), 1, 7);
            chargeRing = Art.Decal(Root, MeshKit.FloorQuad(), chargeMat, 0.06f).transform;
            chargeRing.gameObject.SetActive(false);

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
            bool charging = !hidden && p.Cast != null && p.Cast.Phase == CastPhase.Hold;
            float len = charging ? Mathf.Clamp(sim.Slots[p.Cast.Slot].Card.LockRange, 2f, 9f) : 0f; // карта: дальность автолока
            // посох: линия спецатаки после минимального заряда (MinChargeForAimLine), Ω-атака — две линии, вперёд и назад
            var st = p.Staff;
            bool both = false;
            if (st.Move == StaffMove.Special && st.T >= sim.StaffCfg.specialChargeTime) { charging = true; len = sim.StaffCfg.specialRange; }
            if (st.Move == StaffMove.OmegaAttack) { charging = true; both = true; len = sim.StaffCfg.omegaAttackRange; }
            aimLine.gameObject.SetActive(charging);
            aimLineBack.gameObject.SetActive(charging && both);
            if (charging)
            {
                aimLine.localPosition = new Vector3(a.x * 0.5f, Art.FloorTop + 0.035f, a.y * 0.5f);
                aimLine.rotation = Quaternion.LookRotation(new Vector3(a.x, 0f, a.y));
                aimLine.localScale = new Vector3(0.1f, 1f, len - 0.5f);
                aimLineBack.localPosition = new Vector3(-a.x * 0.5f, Art.FloorTop + 0.035f, -a.y * 0.5f);
                aimLineBack.rotation = Quaternion.LookRotation(new Vector3(-a.x, 0f, -a.y));
                aimLineBack.localScale = aimLine.localScale;
            }
            // посох: рывок — вытянутый силуэт, спринт — наклон, удар — разворот корпуса с косой
            if ((st.Move == StaffMove.Blink && st.BlinkFireT >= 0f) || sim.FormDashing) body.localScale = new Vector3(0.7f, 0.9f, 1.6f);
            // прыжок: рывок над лавой или заряженный — дуга вверх
            float leap = sim.FormLeap;
            if (leap >= 0f) body.localPosition = new Vector3(0f, Mathf.Sin(leap * Mathf.PI) * 1.1f, 0f);
            // заряд формы: кольцо сжимается к полному заряду, в окне «идеального» выпуска — белое и пульсирует
            float charge = 0f;
            bool window = false, missed = false, holding = false;
            if (sim.FormsMode)
                foreach (var fs in sim.Forms)
                    if (fs.Holding) { holding = true; charge = Mathf.Max(charge, sim.ChargeOf(fs)); window |= sim.InChargeWindow(fs); missed |= sim.ChargeMissed(fs); }
            chargeRing.gameObject.SetActive(holding && !hidden);
            if (holding)
            {
                // кольцо сжимается к порогу; в окне — белое и пульсирует; окно пропущено — тусклое
                chargeRing.localScale = Vector3.one * Mathf.Lerp(2.6f, 1.3f, charge) * (window ? 1f + 0.08f * Mathf.Sin(time * 40f) : 1f);
                chargeMat.SetColor("_BaseColor", window ? new Color(1f, 1f, 1f, 1f) : missed ? new Color(0.5f, 0.48f, 0.55f, 0.35f) : new Color(1f, 0.85f, 0.45f, 0.25f + 0.6f * charge));
                if (charge > 0f && !missed) body.localScale = new Vector3(1f + 0.05f * charge, 1f - 0.08f * charge, 1f + 0.05f * charge);
            }
            if (st.Move == StaffMove.Sprint) body.localEulerAngles = new Vector3(22f, 0f, 0f);
            if (st.Move is StaffMove.Swing or StaffMove.DashStrike) body.localEulerAngles = new Vector3(0f, -55f, 0f);
            else if (st.Lock > 0f || st.LockCancel > 0f) body.localEulerAngles = new Vector3(8f, 40f, 0f);
            if (st.Move is StaffMove.OmegaAttack or StaffMove.OmegaCast) body.localScale = Vector3.one * (1f + 0.04f * Mathf.Sin(time * 40f));
            shield.gameObject.SetActive(p.Shield > 0f);
            shield.localScale = Vector3.one * (1.8f + Mathf.Sin(time * 12f) * 0.05f);

            bool aiming = p.Cast != null && (p.Cast.Phase == CastPhase.Aim || (p.Cast.Phase == CastPhase.Casting && sim.Slots[p.Cast.Slot].Card?.Color == Arena.Core.CardColor.Yellow));
            reticle.gameObject.SetActive(aiming && !hidden);
            reticleLine.gameObject.SetActive(aiming && !hidden);
            if (aiming)
            {
                var target = p.Cast.Target;
                reticle.position = Art.W(target, 0.07f);
                reticle.localScale = Vector3.one * (0.9f + Mathf.Sin(time * 10f) * 0.06f);
                var d = target - p.Pos;
                reticleLine.position = Art.W(p.Pos, 0.05f);
                reticleLine.rotation = Quaternion.LookRotation(new Vector3(d.x, 0f, d.y).sqrMagnitude > 0.0001f ? new Vector3(d.x, 0f, d.y) : Vector3.forward);
                reticleLine.localScale = new Vector3(0.12f, 1f, d.magnitude);
            }
        }
    }

    /// <summary>Враг: Ghost, Топотун (DandyAce, Пуфф), Thug, Witch (Hades), Wastrel, Casket (Hades II); полоска здоровья,
    /// иконки статусов, «!» и обозначение атаки.</summary>
    public sealed class EnemyView
    {
        public readonly Enemy E;
        public readonly Transform Root;
        readonly Transform model, body, hpFill, alert, statusRoot, legL, legR, attackIcon;
        readonly Material attackMat;
        TeleShape iconShape = (TeleShape)(-1);
        readonly Renderer[] renderers;
        readonly Material[] flashMats;
        readonly Color[] baseColors;
        readonly TelegraphView tele;
        readonly GameObject spawnDecal;
        readonly Material spawnMat;
        readonly Transform[] statusIcons = new Transform[11];
        static readonly bool[] StatusOn = new bool[11];
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
                case EnemyKind.Witch: BuildWitch(); break;
                case EnemyKind.Wastrel: BuildWastrel(out legL, out legR); break;
                default: BuildCasket(); break;
            }
            if (e.Kind is EnemyKind.Wastrel or EnemyKind.Casket)
                model.localScale = Vector3.one * (e.Kind == EnemyKind.Wastrel ? 0.95f : 0.8f); // Wastrel крупнее Мелинои, Casket — с неё
            renderers = body.GetComponentsInChildren<Renderer>();
            flashMats = new Material[renderers.Length];
            baseColors = new Color[renderers.Length];
            for (int i = 0; i < renderers.Length; i++)
            {
                flashMats[i] = renderers[i].material; // свой экземпляр — для вспышек
                baseColors[i] = flashMats[i].GetColor("_BaseColor");
                if (e.Elite) // мини-босс и босс — тот же враг, красный
                {
                    baseColors[i] = Color.Lerp(baseColors[i], new Color(0.9f, 0.12f, 0.1f, baseColors[i].a), 0.7f);
                    flashMats[i].SetColor("_BaseColor", baseColors[i]);
                }
            }
            if (e.Elite) model.localScale *= 1.3f;

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
            Color[] sc = { new(1f, 0.9f, 0.2f), new(0.7f, 0.42f, 1f), new(0.45f, 0.65f, 1f), new(1f, 0.19f, 0.28f), new(1f, 0.6f, 0.12f), new(0.48f, 0.88f, 0.29f), new(0.85f, 0.4f, 1f),
                new(0.55f, 0.85f, 1f), new(0.55f, 0.92f, 0.3f), new(0.78f, 0.42f, 1f), new(1f, 0.85f, 0.45f) }; // + иней, гниль, приговор, связь
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

            // значок атаки над головой, пока на земле её зона: форма значка — по виду атаки
            attackMat = new Material(Art.DecalMat(Color.white, -1, 24));
            attackIcon = Art.Part(Root, MeshKit.Billboard(), attackMat, new Vector3(0f, Height(e.Kind) + 0.8f, 0f), Vector3.one * 0.55f, default, false).transform;
            attackIcon.gameObject.AddComponent<Billboard>();
            attackIcon.gameObject.SetActive(false);

            spawnMat = Art.DecalInstance(new Color(0.75f, 0.35f, 1f, 0.6f), 1, 1);
            spawnDecal = Art.Decal(Root, MeshKit.FloorQuad(), spawnMat, 0.03f);
            tele = new TelegraphView(parent);
        }

        static float Height(EnemyKind k) => k switch { EnemyKind.Ghost => 1.55f, EnemyKind.Thug => 1.9f, EnemyKind.Witch => 1.45f, EnemyKind.Wastrel => 1.5f, EnemyKind.Casket => 1.3f, _ => 0.9f };

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

        /// <summary>Wastrel — сгорбленная тень-громила Эреба: широкие плечи, маленькая голова, тяжёлые кулаки, ходит.</summary>
        void BuildWastrel(out Transform l, out Transform r)
        {
            var hide = Art.LitMat(new Color(0.30f, 0.24f, 0.36f));
            var dark = Art.LitMat(new Color(0.14f, 0.11f, 0.17f));
            var eye = Art.LitMat(new Color(1f, 0.3f, 0.2f), 3f);
            Art.Part(body, MeshKit.Ico(1, 0.08f, 3), hide, new Vector3(0f, 0.95f, 0f), new Vector3(0.95f, 0.85f, 0.75f));      // туловище
            Art.Part(body, MeshKit.Ico(1, 0.1f, 4), hide, new Vector3(0f, 1.35f, 0.08f), new Vector3(1.05f, 0.45f, 0.6f));     // плечи-горб
            Art.Part(body, MeshKit.Ico(0, 0.05f, 5), dark, new Vector3(0f, 1.3f, 0.38f), new Vector3(0.36f, 0.32f, 0.32f));    // голова низко
            Art.Part(body, MeshKit.Ico(0), eye, new Vector3(-0.08f, 1.33f, 0.53f), Vector3.one * 0.07f, default, false);
            Art.Part(body, MeshKit.Ico(0), eye, new Vector3(0.08f, 1.33f, 0.53f), Vector3.one * 0.07f, default, false);
            // руки до земли и кулаки
            Art.Part(body, MeshKit.Prism(6, 0.8f), hide, new Vector3(-0.55f, 0.45f, 0.15f), new Vector3(0.22f, 0.8f, 0.22f), new Vector3(10f, 0f, -8f));
            Art.Part(body, MeshKit.Prism(6, 0.8f), hide, new Vector3(0.55f, 0.45f, 0.15f), new Vector3(0.22f, 0.8f, 0.22f), new Vector3(10f, 0f, 8f));
            Art.Part(body, MeshKit.Ico(0, 0.1f, 6), dark, new Vector3(-0.58f, 0.38f, 0.22f), Vector3.one * 0.34f);
            Art.Part(body, MeshKit.Ico(0, 0.1f, 7), dark, new Vector3(0.58f, 0.38f, 0.22f), Vector3.one * 0.34f);
            l = Art.Part(model, MeshKit.Prism(5), dark, new Vector3(-0.22f, 0f, 0f), new Vector3(0.2f, 0.45f, 0.2f)).transform;
            r = Art.Part(model, MeshKit.Prism(5), dark, new Vector3(0.22f, 0f, 0f), new Vector3(0.2f, 0.45f, 0.2f)).transform;
        }

        /// <summary>Casket — парящий заклинатель-ларец: узкий балахон без ног, капюшон, светящаяся сфера-заряд перед собой.</summary>
        void BuildCasket()
        {
            var robe = Art.LitMat(new Color(0.22f, 0.16f, 0.32f));
            var trim = Art.LitMat(new Color(0.55f, 0.42f, 0.25f));
            var glow = Art.LitMat(new Color(0.75f, 0.4f, 1f), 2.5f);
            var eye = Art.LitMat(new Color(0.85f, 0.6f, 1f), 3f);
            Art.Part(body, MeshKit.Prism(8, 0.45f), robe, new Vector3(0f, 0.25f, 0f), new Vector3(0.62f, 1.05f, 0.6f));      // балахон
            Art.Part(body, MeshKit.Prism(8, 1.1f, false), trim, new Vector3(0f, 0.25f, 0f), new Vector3(0.64f, 0.1f, 0.62f));  // кайма
            Art.Part(body, MeshKit.Prism(4, 0.9f), trim, new Vector3(0f, 0.95f, 0.05f), new Vector3(0.5f, 0.35f, 0.4f), new Vector3(0f, 45f, 0f)); // ларец-грудь
            Art.Part(body, MeshKit.Ico(1, 0.08f, 2), robe, new Vector3(0f, 1.38f, -0.02f), new Vector3(0.42f, 0.46f, 0.44f));  // капюшон
            Art.Part(body, MeshKit.Ico(0), eye, new Vector3(-0.07f, 1.38f, 0.19f), Vector3.one * 0.06f, default, false);
            Art.Part(body, MeshKit.Ico(0), eye, new Vector3(0.07f, 1.38f, 0.19f), Vector3.one * 0.06f, default, false);
            Art.Part(body, MeshKit.Ico(1), glow, new Vector3(0f, 1f, 0.45f), Vector3.one * 0.2f, default, false);              // заряд
        }

        public void Sync(float dt, float time)
        {
            var e = E;
            t += dt;
            Root.position = Art.W(e.Pos);
            bool spawning = e.State == AiState.Spawn;
            // проявление: последние 1.9 с руны враг Hades тёмным силуэтом проступает из пола
            bool fading = spawning && e.HadesKind && e.T < 1.9f;
            model.gameObject.SetActive(!spawning || fading);
            model.localPosition = fading ? new Vector3(0f, -1.2f * Mathf.Clamp01(e.T / 1.9f), 0f) : Vector3.zero;
            spawnDecal.SetActive(spawning);
            if (spawning)
            {
                // руна призыва Hades (EnemySummonRune) 2 с, у врагов Hades II — 1.8 с; размер по врагу
                float total = e.Kind is EnemyKind.Wastrel or EnemyKind.Casket ? 1.8f : 2f;
                float k = Mathf.Clamp01(1f - e.T / total);
                float rune = e.Kind is EnemyKind.Witch or EnemyKind.Casket ? 1.5f : 2f;
                spawnDecal.transform.localScale = Vector3.one * (rune * (0.5f + 0.5f * k));
                spawnMat.SetColor("_BaseColor", new Color(0.75f, 0.35f, 1f, 0.3f + 0.5f * k));
            }
            var face = e.Face.sqrMagnitude > 0.0001f ? e.Face : Vector2.down;
            model.rotation = Quaternion.Slerp(model.rotation, Quaternion.LookRotation(new Vector3(face.x, 0f, face.y)), Mathf.Clamp01(dt * 12f));

            bool windup = e.State == AiState.Windup;
            float scale = 1f;
            if (windup && e.Tele != null) scale = 1f + 0.3f * e.Tele.Progress; // AttackIncreaseScale 1.3 за время замаха
            if (e.Dead) scale = Mathf.Clamp01(e.DeathT / 0.8f);
            if (e.HadesKind && windup) scale = 1f; // в Hades враг не растёт — только дрожит
            float hover = e.Kind switch
            {
                EnemyKind.Casket => 0.25f + Mathf.Sin(t * 3f) * 0.08f, // Casket парит
                EnemyKind.Ghost or EnemyKind.Witch => Mathf.Sin(t * 3f) * 0.08f,
                _ => 0f,
            };
            float hop = 0f;
            if (e.Kind == EnemyKind.Stomper && (e.State is AiState.Chase or AiState.Wander) && e.Moved > 0.001f) hop = Mathf.Abs(Mathf.Sin(t * 16f)) * 0.08f;
            if (e.Kind == EnemyKind.Wastrel && e.Moved > 0.001f) hop = Mathf.Abs(Mathf.Sin(t * 8f)) * 0.05f;
            // дрожь: от удара (6 ед. Hades, 0.15 с) и в конце замаха врагов Hades (3 ед.)
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

            // вспышки: красная при уроне (0.03 с); пока на земле зона атаки — враг краснеет к удару (15% → 80%),
            // в последней четверти мигает; атака без зоны врага не красит; затем — статусы свойств режима форм
            var threat = e.Dead ? null : e.Tele;
            Color tint = Color.clear;
            if (e.Flash > 0f) tint = new Color(0.86f, 0.08f, 0.08f, 1f);
            else if (threat != null)
            {
                float k = threat.Progress;
                float blink = k > 0.75f && Mathf.Repeat(time * 14f, 1f) >= 0.5f ? 0.6f : 1f;
                tint = new Color(1f, 0.12f, 0.08f, (0.15f + 0.65f * k) * blink);
            }
            else if (e.Frozen > 0f) tint = new Color(0.62f, 0.88f, 1f, 0.75f);                      // заморожен
            else if (e.Burn > 0f) tint = new Color(1f, 0.5f, 0.1f, 0.2f + 0.15f * Mathf.Sin(time * 25f)); // горит
            else if (e.RotT > 0f) tint = new Color(0.45f, 0.8f, 0.2f, 0.3f);                         // гниёт
            else if (e.DoomT > 0f) tint = new Color(0.7f, 0.3f, 1f, 0.2f + 0.2f * Mathf.Repeat(time * 2f, 1f)); // приговорён
            for (int i = 0; i < flashMats.Length; i++)
                flashMats[i].SetColor("_BaseColor", Color.Lerp(baseColors[i], new Color(tint.r, tint.g, tint.b, 1f), tint.a));

            hpFill.localScale = new Vector3(0.86f * Mathf.Clamp01(e.Hp / e.MaxHp), 0.07f, 0.01f);
            hpFill.localPosition = new Vector3(-0.43f * (1f - Mathf.Clamp01(e.Hp / e.MaxHp)), 0f, -0.01f);
            hpFill.parent.gameObject.SetActive(!spawning && !e.Dead);
            alert.gameObject.SetActive(e.Alert > 0f && !e.Dead && threat == null);
            attackIcon.gameObject.SetActive(threat != null);
            if (threat != null)
            {
                if (iconShape != threat.Shape) { iconShape = threat.Shape; attackMat.SetTexture("_BaseMap", Art.AttackIcon(iconShape)); }
                float k = threat.Progress;
                attackMat.SetColor("_BaseColor", new Color(1f, 0.16f, 0.1f, 0.75f + 0.25f * k));
                attackIcon.localScale = Vector3.one * (0.45f + 0.2f * k);
            }
            if (e.Alert > 0f) alert.localScale = Vector3.one * (1f + Mathf.Max(0f, e.Alert - 0.6f) * 2f);

            var on = StatusOn; // общий буфер — без выделения памяти каждый кадр
            on[0] = e.Stun > 0f; on[1] = e.Fear > 0f; on[2] = e.Pull > 0f || e.PullAce > 0f;
            on[3] = HasDot(e, DotKind.Bleed) || e.MarkBleed > 0f; on[4] = HasDot(e, DotKind.Burn) || HasDot(e, DotKind.Lava) || e.Burn > 0f;
            on[5] = HasDot(e, DotKind.Poison) || HasDot(e, DotKind.Noxious) || e.MarkPoison > 0f; on[6] = HasDot(e, DotKind.Hex);
            on[7] = e.Frost > 0 || e.Frozen > 0f; on[8] = e.RotT > 0f; on[9] = e.DoomT > 0f; on[10] = e.LinkT > 0f;
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
            // свои экземпляры материалов (вспышки, круг появления) вместе с объектом не удаляются
            foreach (var m in flashMats) Object.Destroy(m);
            Object.Destroy(spawnMat);
            Object.Destroy(attackMat);
            Object.Destroy(Root.gameObject);
        }
    }

    /// <summary>Обозначение атаки на земле: контур зоны сразу целиком + заливка, которая растёт до контура к моменту удара.
    /// Контур — отдельный меш одной толщины (OutlineWidth) по всей фигуре: он не растягивается вместе с размером зоны.</summary>
    public sealed class TelegraphView
    {
        const float OutlineWidth = 0.045f;

        readonly Transform root, outline, fill, edge;
        readonly Material outlineMat, fillMat, edgeMat;
        readonly Mesh outlineMesh;
        readonly List<Vector2> poly = new();
        TeleShape shape = (TeleShape)(-1);
        Vector4 built = new(-1f, -1f, -1f, -1f);

        public TelegraphView(Transform parent)
        {
            root = new GameObject("Telegraph").transform;
            root.SetParent(parent, false);
            outlineMat = Art.DecalInstance(Art.DangerEdge, -1, 4);
            fillMat = Art.DecalInstance(Art.Danger, 0, 3);
            edgeMat = Art.DecalInstance(new Color(1f, 0.25f, 0.15f, 0.2f), 0, 1);
            edge = Art.Decal(root, MeshKit.FloorQuad(), edgeMat, 0.025f).transform;
            outlineMesh = new Mesh { name = "TelegraphOutline" };
            outline = Art.Decal(root, outlineMesh, outlineMat, 0.04f).transform;
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
                bool lane = shape is TeleShape.Lane or TeleShape.Trapezoid;
                var mesh = shape switch
                {
                    TeleShape.Lane => MeshKit.FloorLane(),
                    TeleShape.Trapezoid => MeshKit.FloorTrapezoid(Mathf.Clamp01(t.Base / Mathf.Max(t.Side, 0.001f))),
                    TeleShape.Fan => MeshKit.FloorFan(Mathf.Round(t.Angle)),
                    _ => MeshKit.FloorQuad(),
                };
                fill.GetComponent<MeshFilter>().sharedMesh = mesh;
                edge.GetComponent<MeshFilter>().sharedMesh = mesh;
                fillMat.SetTexture("_BaseMap", lane ? null : Art.Disc());
                edgeMat.SetTexture("_BaseMap", lane ? null : Art.Disc());
                built = new Vector4(-1f, -1f, -1f, -1f);
            }
            float k = t.Progress;
            root.position = Art.W(t.Center);
            root.rotation = Quaternion.LookRotation(new Vector3(t.Dir.x, 0f, t.Dir.y));
            BuildOutline(t);
            if (t.Shape == TeleShape.Ellipse)
            {
                var full = new Vector3(t.Side * 2f, 1f, t.Forward * 2f);
                edge.localScale = full;
                fill.localScale = new Vector3(full.x * k, 1f, full.z * k);
            }
            else if (t.Shape == TeleShape.Trapezoid)
            {
                edge.localScale = new Vector3(t.Side, 1f, t.Forward);
                fill.localScale = new Vector3(t.Side, 1f, t.Forward * k);
            }
            else if (t.Shape == TeleShape.Fan)
            {
                edge.localScale = Vector3.one * t.Forward;
                fill.localScale = Vector3.one * (t.Forward * k);
            }
            else
            {
                edge.localScale = new Vector3(t.Side * 2f, 1f, t.Forward);
                fill.localScale = new Vector3(t.Side * 2f, 1f, t.Forward * k);
            }
            // к концу замаха — ярче и мигает, чтобы не пропустить момент удара
            float pulse = k > 0.75f ? (Mathf.Repeat(time * 14f, 1f) < 0.5f ? 1f : 0.65f) : 1f;
            // всё красное: контур, растущая заливка и подложка всей зоны
            outlineMat.SetColor("_BaseColor", new Color(1f, 0.22f, 0.14f, (0.8f + 0.2f * k) * pulse));
            fillMat.SetColor("_BaseColor", new Color(1f, 0.12f, 0.08f, (0.45f + 0.4f * k) * pulse));
            edgeMat.SetColor("_BaseColor", new Color(0.9f, 0.1f, 0.08f, 0.28f));
        }

        /// <summary>Контур в настоящих размерах фигуры (x — поперёк, z — вперёд); перестраивается, только когда размеры меняются.</summary>
        void BuildOutline(Telegraph t)
        {
            var key = new Vector4(t.Forward, t.Side, t.Base, t.Angle);
            if ((key - built).sqrMagnitude < 1e-6f) return;
            built = key;
            poly.Clear();
            switch (t.Shape)
            {
                case TeleShape.Ellipse:
                    for (int i = 0; i < 48; i++)
                    {
                        float a = i * Mathf.PI * 2f / 48f;
                        poly.Add(new Vector2(Mathf.Sin(a) * t.Side, Mathf.Cos(a) * t.Forward));
                    }
                    break;
                case TeleShape.Trapezoid:
                    poly.Add(new Vector2(-t.Base, 0f)); poly.Add(new Vector2(-t.Side, t.Forward));
                    poly.Add(new Vector2(t.Side, t.Forward)); poly.Add(new Vector2(t.Base, 0f));
                    break;
                case TeleShape.Fan:
                {
                    poly.Add(Vector2.zero);
                    int n = Mathf.Max(6, Mathf.CeilToInt(t.Angle * 2f / 8f));
                    for (int i = 0; i <= n; i++)
                    {
                        float a = Mathf.Deg2Rad * (-t.Angle + 2f * t.Angle * i / n);
                        poly.Add(new Vector2(Mathf.Sin(a), Mathf.Cos(a)) * t.Forward);
                    }
                    break;
                }
                default:
                    poly.Add(new Vector2(-t.Side, 0f)); poly.Add(new Vector2(-t.Side, t.Forward));
                    poly.Add(new Vector2(t.Side, t.Forward)); poly.Add(new Vector2(t.Side, 0f));
                    break;
            }
            MeshKit.Stroke(outlineMesh, poly, OutlineWidth);
            outline.localScale = Vector3.one;
        }

        public void Destroy()
        {
            Object.Destroy(outlineMesh);
            Object.Destroy(outlineMat); Object.Destroy(fillMat); Object.Destroy(edgeMat);
            Object.Destroy(root.gameObject);
        }
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

    /// <summary>Мерцание огня жаровни: яркость света и размер пламени дрожат.</summary>
    public sealed class Flicker : MonoBehaviour
    {
        public Transform Flame;
        public float Seed;
        Light lt;
        float baseIntensity;
        Vector3 baseScale;

        void Start()
        {
            lt = GetComponent<Light>();
            baseIntensity = lt != null ? lt.intensity : 0f;
            if (Flame != null) baseScale = Flame.localScale;
        }

        void Update()
        {
            float t = Time.time * 7f + Seed;
            float k = 0.85f + 0.15f * Mathf.PerlinNoise(t, Seed) + 0.05f * Mathf.Sin(t * 3.1f);
            if (lt != null) lt.intensity = baseIntensity * k;
            if (Flame != null) Flame.localScale = new Vector3(baseScale.x, baseScale.y * (0.8f + 0.4f * Mathf.PerlinNoise(Seed, t)), baseScale.z);
        }
    }
}
