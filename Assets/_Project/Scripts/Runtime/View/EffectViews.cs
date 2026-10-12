using System.Collections.Generic;
using Arena.Sim;
using UnityEngine;

namespace Arena.View
{
    /// <summary>Отрисовка эффектов карт по Id из симуляции и одноразовых вспышек по событиям.</summary>
    public sealed class EffectViews
    {
        readonly Transform root;
        readonly Dictionary<int, EffectView> views = new();
        readonly List<int> stale = new();
        readonly List<Flash> flashes = new();
        readonly List<ZapLine> zaps = new();
        // пулы: кольцо (внешнее и внутреннее), вспышка-сфера, молния — объекты и материалы переиспользуются
        readonly Stack<Flash>[] freeFlashes = { new(), new(), new() };
        readonly Stack<LineRenderer> freeZaps = new();

        public EffectViews(Transform parent)
        {
            root = new GameObject("Effects").transform;
            root.SetParent(parent, false);
        }

        public void Sync(ArenaSim sim, float dt, float time)
        {
            foreach (var f in sim.Effects)
            {
                if (f.Done) continue;
                if (!views.TryGetValue(f.Id, out var v)) views[f.Id] = v = new EffectView(f, root, sim);
                v.Sync(sim, dt, time);
            }
            stale.Clear();
            foreach (var kv in views) if (kv.Value.F.Done || !sim.Effects.Contains(kv.Value.F)) stale.Add(kv.Key);
            foreach (var id in stale) { views[id].Destroy(); views.Remove(id); }

            for (int i = zaps.Count - 1; i >= 0; i--)
            {
                var z = zaps[i];
                z.T += dt;
                if (z.T >= z.Life) { z.Line.gameObject.SetActive(false); freeZaps.Push(z.Line); zaps.RemoveAt(i); continue; }
                var c = z.Color; c.a *= 1f - z.T / z.Life;
                z.Line.startColor = z.Line.endColor = c;
            }

            for (int i = flashes.Count - 1; i >= 0; i--)
            {
                var fl = flashes[i];
                fl.T += dt;
                float k = fl.T / fl.Life;
                if (k >= 1f) { fl.Go.SetActive(false); freeFlashes[fl.Kind].Push(fl); flashes.RemoveAt(i); continue; }
                fl.Go.transform.localScale = fl.From + (fl.To - fl.From) * (1f - (1f - k) * (1f - k));
                var c = fl.Color; c.a *= 1f - k;
                fl.Mat.SetColor("_BaseColor", c);
            }
        }

        public void Clear()
        {
            foreach (var v in views.Values) v.Destroy();
            views.Clear();
        }

        /// <summary>Кольцо на полу, расширяется и гаснет.</summary>
        public void Ring(Vector2 pos, float radius, Color color, float life = 0.35f)
        {
            Spawn(0, Art.W(pos, Art.FloorTop + 0.05f), color, life, Vector3.one * radius * 1.2f, Vector3.one * radius * 2.1f);
            Spawn(1, Art.W(pos, Art.FloorTop + 0.045f), new Color(color.r, color.g, color.b, color.a * 0.45f), life * 0.8f, Vector3.one * radius * 1.6f, Vector3.one * radius * 2f);
        }

        /// <summary>Вспышка из пула: 0 — кольцо, 1 — заливка кольца, 2 — сфера.</summary>
        void Spawn(int kind, Vector3 at, Color color, float life, Vector3 from, Vector3 to)
        {
            Flash fl;
            if (freeFlashes[kind].Count > 0) { fl = freeFlashes[kind].Pop(); fl.Go.SetActive(true); }
            else
            {
                fl = new Flash { Kind = kind };
                if (kind == 2)
                {
                    fl.Mat = new Material(Art.LitMat(Color.white, 2f, true));
                    fl.Go = Art.Part(root, MeshKit.Ico(1), fl.Mat, Vector3.zero, Vector3.one, default, false);
                }
                else
                {
                    fl.Mat = Art.DecalInstance(Color.white, kind == 0 ? 1 : 0, kind == 0 ? 6 : 5);
                    fl.Go = Art.Decal(root, MeshKit.FloorQuad(), fl.Mat, 0.05f);
                }
            }
            fl.Go.transform.position = at;
            fl.Go.transform.localScale = from;
            fl.Color = color; fl.Life = life; fl.T = 0f; fl.From = from; fl.To = to;
            fl.Mat.SetColor("_BaseColor", color);
            if (kind == 2) fl.Mat.SetColor("_EmissionColor", color * 2f);
            flashes.Add(fl);
        }

        /// <summary>Молния: ломаная от a до b, гаснет за 0.15 с.</summary>
        public void Zap(Vector2 a, Vector2 b, Color color)
        {
            LineRenderer line;
            if (freeZaps.Count > 0) { line = freeZaps.Pop(); line.gameObject.SetActive(true); }
            else
            {
                var go = new GameObject("Zap");
                go.transform.SetParent(root, false);
                line = go.AddComponent<LineRenderer>();
                line.sharedMaterial = Art.LitMat(Color.white, 3f, true);
                line.widthMultiplier = 0.08f;
                line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            }
            const int n = 7;
            line.positionCount = n;
            var side = new Vector2(-(b - a).y, (b - a).x).normalized;
            for (int i = 0; i < n; i++)
            {
                float k = (float)i / (n - 1);
                float off = i == 0 || i == n - 1 ? 0f : Random.Range(-0.25f, 0.25f);
                line.SetPosition(i, Art.W(Vector2.Lerp(a, b, k) + side * off, 0.8f));
            }
            line.startColor = line.endColor = color;
            zaps.Add(new ZapLine { Line = line, Color = color, Life = 0.15f });
        }

        sealed class ZapLine
        {
            public LineRenderer Line;
            public Color Color;
            public float T, Life;
        }

        /// <summary>Вспышка-сфера (попадание, телепорт).</summary>
        public void Puff(Vector2 pos, float size, Color color, float life = 0.25f, float y = 0.6f) =>
            Spawn(2, Art.W(pos, y), color, life, Vector3.one * size * 0.5f, Vector3.one * size * 1.3f);

        sealed class Flash
        {
            public int Kind;
            public GameObject Go;
            public Material Mat;
            public Color Color;
            public float T, Life;
            public Vector3 From, To;
        }
    }

    static class ColorExt
    {
        public static Color WithA(this Color c, float a) => new(c.r, c.g, c.b, a);
    }

    sealed class EffectView
    {
        public readonly Effect F;
        readonly Transform root;
        readonly List<Transform> parts = new();
        readonly List<Material> owned = new();
        LineRenderer line;
        int builtPoints;

        public EffectView(Effect f, Transform parent, ArenaSim sim)
        {
            F = f;
            root = new GameObject(f.Kind.ToString()).transform;
            root.SetParent(parent, false);
            switch (f.Kind)
            {
                case EffectKind.Dagger:
                    Add(Art.Part(root, MeshKit.Prism(4, 0f), Art.LitMat(new Color(1f, 0.75f, 0.98f), 1.5f), new Vector3(0f, 0.7f, -0.25f), new Vector3(0.2f, 0.75f, 0.08f), new Vector3(90f, 0f, 0f), false));
                    Add(Art.Part(root, MeshKit.Box(), Art.LitMat(Art.Pink, 1f), new Vector3(0f, 0.7f, -0.35f), new Vector3(0.22f, 0.06f, 0.08f), default, false));
                    Trail(root, Art.Pink, 0.18f, 0.25f, 0.7f);
                    break;
                case EffectKind.FiveCard:
                case EffectKind.WildCard:
                    Add(Art.Part(root, MeshKit.Box(), Art.LitMat(new Color(1f, 0.97f, 0.92f), 0.6f), new Vector3(0f, 0.7f, 0f), new Vector3(0.22f, 0.02f, 0.32f), default, false));
                    Add(Art.Part(root, MeshKit.Box(), Art.LitMat(Art.Pink, 1.5f), new Vector3(0f, 0.712f, 0f), new Vector3(0.1f, 0.01f, 0.16f), default, false));
                    Trail(root, Art.Pink, 0.12f, 0.15f, 0.7f);
                    break;
                case EffectKind.Missile:
                    Add(Art.Part(root, MeshKit.Ico(1), Art.LitMat(new Color(1f, 0.75f, 0.3f), 2.5f), new Vector3(0f, 0.7f, 0f), Vector3.one * (0.35f + 0.25f * f.Hold), default, false));
                    Trail(root, new Color(1f, 0.55f, 0.2f), 0.25f, 0.3f, 0.7f);
                    break;
                case EffectKind.Rose:
                    Add(Art.Part(root, MeshKit.Ico(0), Art.LitMat(new Color(0.9f, 0.1f, 0.4f), 0.8f), new Vector3(0f, 0.7f, 0f), Vector3.one * 0.26f, default, false));
                    Add(Art.Part(root, MeshKit.Prism(4, 0f), Art.LitMat(new Color(0.35f, 0.75f, 0.3f)), new Vector3(0f, 0.6f, -0.12f), new Vector3(0.12f, 0.2f, 0.12f), new Vector3(-90f, 0f, 0f), false));
                    Trail(root, new Color(0.5f, 0.95f, 0.4f), 0.1f, 0.2f, 0.7f);
                    break;
                case EffectKind.Bat:
                {
                    var bat = Art.LitMat(new Color(1f, 0.38f, 0.85f), 0.8f);
                    Add(Art.Part(root, MeshKit.Ico(1), Art.LitMat(new Color(0.45f, 0.08f, 0.4f)), new Vector3(0f, 1f, 0f), Vector3.one * 0.3f, default, true));
                    Add(Art.Part(root, MeshKit.Wedge(), bat, new Vector3(-0.3f, 1f, 0f), new Vector3(0.7f, 0.4f, 0.4f), new Vector3(0f, -90f, 0f), true));
                    Add(Art.Part(root, MeshKit.Wedge(), bat, new Vector3(0.3f, 1f, 0f), new Vector3(0.7f, 0.4f, 0.4f), new Vector3(0f, 90f, 0f), true));
                    Trail(root, Art.Pink, 0.2f, 0.2f, 1f);
                    break;
                }
                case EffectKind.BlackHole:
                {
                    var m = Art.DecalInstance(new Color(0.12f, 0.02f, 0.25f, 0.85f), 3, 4); owned.Add(m);
                    var d = Art.Decal(root, MeshKit.FloorQuad(), m, 0.04f); d.transform.localScale = Vector3.one * f.R * 2.2f; Add(d);
                    var rm = Art.DecalInstance(new Color(0.7f, 0.5f, 1f, 0.8f), 1, 5); owned.Add(rm);
                    var r1 = Art.Decal(root, MeshKit.FloorQuad(), rm, 0.05f); r1.transform.localScale = Vector3.one * f.R * 2f; Add(r1);
                    Add(Art.Part(root, MeshKit.Ico(1), Art.LitMat(new Color(0.05f, 0.0f, 0.1f)), new Vector3(0f, 0.6f, 0f), Vector3.one * 0.7f, default, false));
                    for (int i = 0; i < 6; i++)
                        Add(Art.Part(root, MeshKit.Ico(0), Art.LitMat(new Color(0.75f, 0.6f, 1f), 2f), Vector3.zero, Vector3.one * 0.15f, default, false));
                    break;
                }
                case EffectKind.Cone:
                {
                    var m = Art.DecalInstance(new Color(1f, 0.95f, 0.55f, 0.7f), -1, 4); owned.Add(m);
                    var c = Art.Decal(root, MeshKit.FloorFan(35f), m, 0.06f);
                    c.transform.localScale = Vector3.one * f.R; Add(c);
                    break;
                }
                case EffectKind.DashHit:
                    Trail(root, Art.Blue, 0.6f, 0.25f, 0.6f);
                    break;
                case EffectKind.Cloud:
                {
                    var m = Art.DecalInstance(new Color(0.45f, 0.85f, 0.25f, 0.35f), 3, 4); owned.Add(m);
                    var d = Art.Decal(root, MeshKit.FloorQuad(), m, 0.04f); d.transform.localScale = Vector3.one * f.R * 2.2f; Add(d);
                    var puff = Art.LitMat(new Color(0.5f, 0.85f, 0.35f, 0.45f), 0.4f, true);
                    var rnd = new System.Random(f.Id);
                    for (int i = 0; i < 9; i++)
                    {
                        float a = (float)rnd.NextDouble() * Mathf.PI * 2f, rr = (float)rnd.NextDouble() * f.R * 0.75f;
                        Add(Art.Part(root, MeshKit.Ico(1), puff, new Vector3(Mathf.Cos(a) * rr, 0.3f, Mathf.Sin(a) * rr), Vector3.one * (0.6f + (float)rnd.NextDouble() * 0.6f), default, false));
                    }
                    break;
                }
                case EffectKind.StaffWedge:
                {
                    // хитбокс удара — светящаяся трапеция на полу (у ударов комбо — клин от вершины)
                    var col = f.Phase == 4 ? new Color(0.75f, 0.55f, 1f, 0.8f) : new Color(0.65f, 1f, 0.95f, 0.75f);
                    var m = Art.DecalInstance(col, -1, 6); owned.Add(m);
                    var d = Art.Decal(root, MeshKit.FloorTrapezoid(Mathf.Clamp01(f.Back / Mathf.Max(f.R, 0.001f))), m, 0.05f);
                    d.transform.localPosition = new Vector3(0f, Art.FloorTop + 0.05f, -f.Length);
                    d.transform.localScale = new Vector3(f.R, 1f, f.Length);
                    Add(d);
                    break;
                }
                case EffectKind.StaffBall:
                {
                    float s = f.Phase == 1 ? 0.55f : 0.28f;
                    Add(Art.Part(root, MeshKit.Ico(1), Art.LitMat(new Color(0.45f, 1f, 0.9f), 3f), new Vector3(0f, 0.7f, 0f), Vector3.one * s, default, false));
                    Add(Art.Part(root, MeshKit.Ico(0), Art.LitMat(Color.white, 4f), new Vector3(0f, 0.7f, 0f), Vector3.one * s * 0.5f, default, false));
                    Trail(root, new Color(0.4f, 0.95f, 0.85f), s * 0.8f, 0.15f, 0.7f);
                    break;
                }
                case EffectKind.CastCircle:
                {
                    var rm = Art.DecalInstance(new Color(0.55f, 0.95f, 0.9f, 0.85f), 1, 2); owned.Add(rm);
                    Add(Art.Decal(root, MeshKit.FloorQuad(), rm, 0.03f));
                    var fm = Art.DecalInstance(new Color(0.35f, 0.75f, 0.75f, 0.18f), 0, 1); owned.Add(fm);
                    Add(Art.Decal(root, MeshKit.FloorQuad(), fm, 0.025f));
                    break;
                }
                case EffectKind.EnemyBolt:
                    Add(Art.Part(root, MeshKit.Ico(1), Art.LitMat(new Color(0.75f, 0.4f, 1f), 3f), new Vector3(0f, 0.65f, 0f), Vector3.one * 0.32f, default, false));
                    Add(Art.Part(root, MeshKit.Ico(0), Art.LitMat(new Color(1f, 0.9f, 1f), 4f), new Vector3(0f, 0.65f, 0f), Vector3.one * 0.16f, default, false));
                    Trail(root, new Color(0.7f, 0.35f, 1f), 0.2f, 0.25f, 0.65f);
                    break;
                case EffectKind.Tether:
                    line = root.gameObject.AddComponent<LineRenderer>();
                    line.sharedMaterial = Art.LitMat(new Color(1f, 0.92f, 0.5f), 3f);
                    line.widthMultiplier = 0.12f; line.positionCount = 2;
                    line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    break;
                default:
                    if (f.Kind >= EffectKind.FormShot) BuildForm(f);
                    break;
            }
        }

        // ---------- режим форм ----------

        Color tint;

        void BuildForm(Effect f)
        {
            tint = Art.CardTint(f.Card);
            switch (f.Kind)
            {
                case EffectKind.FormShot:
                    Add(Art.Part(root, MeshKit.Ico(1), Art.LitMat(tint, 3f), new Vector3(0f, 0.75f, 0f), Vector3.one * 0.34f, default, false));
                    Add(Art.Part(root, MeshKit.Ico(0), Art.LitMat(Color.white, 4f), new Vector3(0f, 0.75f, 0f), Vector3.one * 0.16f, default, false));
                    Trail(root, tint, 0.26f, 0.2f, 0.75f);
                    break;
                case EffectKind.FormBolt:
                    Add(Art.Part(root, MeshKit.Ico(0), Art.LitMat(tint, 3f), new Vector3(0f, 0.85f, 0f), Vector3.one * 0.22f, default, false));
                    Trail(root, tint, 0.14f, 0.15f, 0.85f);
                    break;
                case EffectKind.FormZone:
                {
                    var fm = Art.DecalInstance(new Color(tint.r, tint.g, tint.b, 0.16f), f.Phase == 1 ? -1 : 0, 1); owned.Add(fm);
                    Add(Art.Decal(root, f.Phase == 1 ? MeshKit.FloorLane() : MeshKit.FloorQuad(), fm, 0.026f));
                    var rm = Art.DecalInstance(new Color(tint.r, tint.g, tint.b, 0.8f), f.Phase == 1 ? 2 : 1, 2); owned.Add(rm);
                    Add(Art.Decal(root, f.Phase == 1 ? MeshKit.FloorLane() : MeshKit.FloorQuad(), rm, 0.03f));
                    for (int i = 0; i < 5; i++)
                        Add(Art.Part(root, MeshKit.Ico(0), Art.LitMat(tint, 2.5f), Vector3.zero, Vector3.one * 0.12f, default, false));
                    break;
                }
                case EffectKind.FormSpirit:
                {
                    // дух: бледный череп в саване, глаза цвета свойства
                    var shroud = Art.LitMat(new Color(0.55f, 0.6f, 0.68f, 0.55f), 0.6f, true);
                    Add(Art.Part(root, MeshKit.Prism(7, 0.2f), shroud, new Vector3(0f, 0.55f, 0f), new Vector3(0.42f, 0.6f, 0.42f), new Vector3(180f, 0f, 0f), false));
                    Add(Art.Part(root, MeshKit.Ico(1), Art.LitMat(new Color(0.86f, 0.83f, 0.76f), 0.3f), new Vector3(0f, 1.15f, 0f), new Vector3(0.32f, 0.34f, 0.3f), default, false));
                    Add(Art.Part(root, MeshKit.Ico(0), Art.LitMat(tint, 4f), new Vector3(-0.07f, 1.17f, 0.13f), Vector3.one * 0.07f, default, false));
                    Add(Art.Part(root, MeshKit.Ico(0), Art.LitMat(tint, 4f), new Vector3(0.07f, 1.17f, 0.13f), Vector3.one * 0.07f, default, false));
                    if (f.Card.Has(Arena.Core.Trait.Lightning))
                    {
                        line = new GameObject("Arc").AddComponent<LineRenderer>();
                        line.transform.SetParent(root, false);
                        line.sharedMaterial = Art.LitMat(Art.TraitColor(Arena.Core.Trait.Lightning), 3f, true);
                        line.widthMultiplier = 0.07f; line.positionCount = 6;
                        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    }
                    break;
                }
                case EffectKind.FormBarrier:
                case EffectKind.FormWall:
                {
                    float a = f.Kind == EffectKind.FormWall ? 0.18f : 0.26f;
                    Add(Art.Part(root, MeshKit.Ico(1), Art.LitMat(new Color(tint.r, tint.g, tint.b, a), 1.4f, true), new Vector3(0f, 0.7f, 0f), Vector3.one, default, false));
                    var rm = Art.DecalInstance(new Color(tint.r, tint.g, tint.b, 0.85f), 1, 3); owned.Add(rm);
                    Add(Art.Decal(root, MeshKit.FloorQuad(), rm, 0.04f));
                    break;
                }
                case EffectKind.FormTrail:
                {
                    var c = f.Phase == 0 ? Art.TraitColor(Arena.Core.Trait.Fire) : Art.TraitColor(Arena.Core.Trait.Frost);
                    var m = Art.DecalInstance(new Color(c.r, c.g, c.b, 0.45f), -1, 2); owned.Add(m);
                    var d = Art.Decal(root, MeshKit.FloorLane(), m, 0.028f);
                    Add(d);
                    for (int i = 0; i < 6; i++)
                        Add(Art.Part(root, MeshKit.Prism(4, 0f), Art.LitMat(c, 2.5f), Vector3.zero, new Vector3(0.22f, 0.4f, 0.22f), default, false));
                    break;
                }
                case EffectKind.FormShade:
                    Add(Art.Part(root, MeshKit.Prism(9, 0.38f), Art.LitMat(new Color(tint.r, tint.g, tint.b, 0.35f), 1.5f, true), Vector3.zero, new Vector3(0.6f, 1f, 1.1f), default, false));
                    Trail(root, tint, 0.5f, 0.25f, 0.5f);
                    break;
                case EffectKind.FormVortex:
                {
                    var m = Art.DecalInstance(new Color(0.18f, 0.05f, 0.35f, 0.7f), 3, 2); owned.Add(m);
                    var d = Art.Decal(root, MeshKit.FloorQuad(), m, 0.03f); d.transform.localScale = Vector3.one * f.R * 2.2f; Add(d);
                    for (int i = 0; i < 6; i++)
                        Add(Art.Part(root, MeshKit.Ico(0), Art.LitMat(Art.TraitColor(Arena.Core.Trait.Pull), 2.5f), Vector3.zero, Vector3.one * 0.12f, default, false));
                    break;
                }
                case EffectKind.FormDashReturn:
                {
                    var m = Art.DecalInstance(new Color(tint.r, tint.g, tint.b, 0.6f), 1, 2); owned.Add(m);
                    var d = Art.Decal(root, MeshKit.FloorQuad(), m, 0.03f); d.transform.localScale = Vector3.one * 0.9f; Add(d);
                    break;
                }
            }
        }

        void SyncForm(Effect f, ArenaSim sim, float time)
        {
            switch (f.Kind)
            {
                case EffectKind.FormShot:
                    root.localScale = Vector3.one * f.Grow * (0.85f + 0.3f * f.Mul);
                    break;
                case EffectKind.FormZone:
                {
                    root.rotation = f.Phase == 1 ? Quaternion.LookRotation(new Vector3(f.Dir.x, 0f, f.Dir.y)) : Quaternion.identity;
                    float fade = Mathf.Clamp01(Mathf.Min(f.Age * 6f, f.Phase == 2 ? 1f : f.Life * 3f + 0.25f));
                    if (f.Phase == 1)
                    {
                        root.position = Art.W(f.Start);
                        float len = (f.To - f.Start).magnitude, w = sim.FormCfg.zoneRiftWidth * 2f * f.Grow;
                        parts[0].localScale = parts[1].localScale = new Vector3(w, 1f, len);
                    }
                    else parts[0].localScale = parts[1].localScale = Vector3.one * (f.R * f.Grow * 2f);
                    float pulse = 0.75f + 0.25f * Mathf.Sin(time * 6f + f.Id);
                    owned[0].SetColor("_BaseColor", new Color(tint.r, tint.g, tint.b, 0.16f * fade * f.Mul + 0.04f));
                    owned[1].SetColor("_BaseColor", new Color(tint.r, tint.g, tint.b, 0.8f * fade * pulse));
                    for (int i = 0; i < 5; i++)
                    {
                        float a = time * 1.6f + i * 1.2566f;
                        float rr = f.Phase == 1 ? 0f : f.R * f.Grow * 0.8f;
                        var pos = f.Phase == 1
                            ? new Vector3(0f, 0.25f + 0.15f * Mathf.Sin(time * 3f + i), (f.To - f.Start).magnitude * Mathf.Repeat(time * 0.4f + i * 0.2f, 1f))
                            : new Vector3(Mathf.Cos(a) * rr, 0.25f + 0.15f * Mathf.Sin(time * 3f + i), Mathf.Sin(a) * rr);
                        parts[2 + i].localPosition = pos;
                        parts[2 + i].gameObject.SetActive(fade > 0.3f);
                    }
                    break;
                }
                case EffectKind.FormSpirit:
                {
                    var face = sim.Player.Face;
                    root.rotation = Quaternion.LookRotation(new Vector3(face.x, 0f, face.y));
                    root.localScale = Vector3.one * (f.Grow * (0.8f + 0.2f * f.Mul));
                    root.position += Vector3.up * (0.12f * Mathf.Sin(time * 3f + f.Id));
                    if (line != null)
                    {
                        var a = sim.Player.Pos; var b = f.Pos;
                        var side = new Vector2(-(b - a).y, (b - a).x).normalized;
                        for (int i = 0; i < 6; i++)
                        {
                            float k = i / 5f;
                            float off = i == 0 || i == 5 ? 0f : Mathf.Sin(time * 40f + i * 2.1f) * 0.18f;
                            line.SetPosition(i, Art.W(Vector2.Lerp(a, b, k) + side * off, 0.8f));
                        }
                        line.useWorldSpace = true;
                    }
                    break;
                }
                case EffectKind.FormBarrier:
                case EffectKind.FormWall:
                {
                    root.rotation = Quaternion.identity;
                    float s = f.R * f.Grow * 2f * (1f + 0.04f * Mathf.Sin(time * 12f));
                    parts[0].localScale = Vector3.one * s;
                    parts[1].localScale = Vector3.one * s;
                    break;
                }
                case EffectKind.FormTrail:
                {
                    root.position = Vector3.zero; root.rotation = Quaternion.identity;
                    var a = f.Points[0]; var b = f.Points[1];
                    var d = b - a;
                    float len = Mathf.Max(0.05f, d.magnitude);
                    parts[0].position = Art.W(a, Art.FloorTop + 0.028f);
                    parts[0].rotation = Quaternion.LookRotation(new Vector3(d.x, 0f, d.y).normalized + new Vector3(0f, 0f, 1e-4f));
                    parts[0].localScale = new Vector3(f.R * 2f, 1f, len);
                    float fade = Mathf.Clamp01(f.Life);
                    owned[0].SetColor("_BaseColor", owned[0].GetColor("_BaseColor").WithA(0.45f * fade));
                    for (int i = 0; i < 6; i++)
                    {
                        var pt = Vector2.Lerp(a, b, (i + 0.5f) / 6f);
                        parts[1 + i].position = Art.W(pt, 0.2f);
                        parts[1 + i].localScale = new Vector3(0.22f, (0.3f + 0.15f * Mathf.Sin(time * 16f + i)) * fade, 0.22f);
                    }
                    break;
                }
                case EffectKind.FormShade:
                    root.gameObject.SetActive(f.T <= 0f);
                    break;
                case EffectKind.FormVortex:
                    root.rotation = Quaternion.identity;
                    for (int i = 0; i < 6; i++)
                    {
                        float a = -time * (4f + i * 0.5f) + i;
                        float rr = f.R * (1f - Mathf.Repeat(time * 1.2f + i * 0.17f, 1f));
                        parts[1 + i].localPosition = new Vector3(Mathf.Cos(a) * rr, 0.3f, Mathf.Sin(a) * rr);
                    }
                    break;
                case EffectKind.FormDashReturn:
                    root.rotation = Quaternion.identity;
                    root.localScale = Vector3.one * (0.7f + 0.3f * Mathf.Sin(time * 10f));
                    break;
            }
        }

        void Add(GameObject go) => parts.Add(go.transform);

        static void Trail(Transform t, Color c, float width, float time, float y)
        {
            var holder = new GameObject("Trail");
            holder.transform.SetParent(t, false);
            holder.transform.localPosition = new Vector3(0f, y, 0f);
            var tr = holder.AddComponent<TrailRenderer>();
            tr.time = time; tr.widthMultiplier = width; tr.minVertexDistance = 0.05f;
            tr.sharedMaterial = Art.LitMat(c, 2f, true);
            tr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            tr.widthCurve = AnimationCurve.Linear(0f, 1f, 1f, 0f);
            tr.startColor = new Color(c.r, c.g, c.b, 0.8f); tr.endColor = new Color(c.r, c.g, c.b, 0f);
        }

        public void Sync(ArenaSim sim, float dt, float time)
        {
            var f = F;
            root.position = Art.W(f.Pos);
            if (f.Dir.sqrMagnitude > 0.0001f) root.rotation = Quaternion.LookRotation(new Vector3(f.Dir.x, 0f, f.Dir.y));
            switch (f.Kind)
            {
                case EffectKind.Dagger:
                    // пока висят — покачиваются и разгораются
                    if (f.T > 0f) root.position += Vector3.up * Mathf.Sin(time * 25f) * 0.03f;
                    break;
                case EffectKind.FiveCard:
                case EffectKind.WildCard:
                    parts[0].localRotation = Quaternion.Euler(0f, time * 900f, 0f);
                    break;
                case EffectKind.Bat:
                {
                    var target = f.Phase == 2 ? sim.Player.Pos - f.Pos : f.Dir;
                    if (target.sqrMagnitude > 0.0001f) root.rotation = Quaternion.LookRotation(new Vector3(target.x, 0f, target.y));
                    float flap = Mathf.Sin(time * 30f) * 35f;
                    parts[1].localEulerAngles = new Vector3(0f, -90f, flap);
                    parts[2].localEulerAngles = new Vector3(0f, 90f, -flap);
                    break;
                }
                case EffectKind.BlackHole:
                {
                    float k = Mathf.Min(1f, f.Age * 5f, f.Life * 3f);
                    root.localScale = Vector3.one * k;
                    parts[1].localRotation = Quaternion.Euler(0f, time * 120f, 0f);
                    for (int i = 0; i < 6; i++)
                    {
                        float a = time * (2.5f + i * 0.4f) + i;
                        float rr = f.R * (0.3f + 0.6f * Mathf.Repeat(time * 0.7f + i * 0.17f, 1f));
                        parts[3 + i].localPosition = new Vector3(Mathf.Cos(a) * rr, 0.35f, Mathf.Sin(a) * rr);
                    }
                    break;
                }
                case EffectKind.Cone:
                    owned[0].SetColor("_BaseColor", new Color(1f, 0.95f, 0.55f, 0.75f * Mathf.Clamp01(f.Life / 0.25f)));
                    break;
                case EffectKind.Cloud:
                {
                    float k = Mathf.Clamp01(Mathf.Min(f.Age * 4f, f.Life * 2f));
                    root.localScale = Vector3.one * (0.6f + 0.4f * k);
                    for (int i = 1; i < parts.Count; i++) parts[i].localPosition += Vector3.up * (Mathf.Sin(time * 2f + i) * 0.002f);
                    break;
                }
                case EffectKind.StaffWedge:
                    owned[0].SetColor("_BaseColor", owned[0].GetColor("_BaseColor") * new Color(1f, 1f, 1f, 0.85f));
                    parts[0].gameObject.SetActive(f.T <= 0f);
                    break;
                case EffectKind.StaffBall:
                    parts[0].gameObject.SetActive(f.T <= 0f);
                    parts[1].gameObject.SetActive(f.T <= 0f);
                    break;
                case EffectKind.CastCircle:
                {
                    // круг растёт ~0.2 с; заряженный Ω-кастом — ярче и пульсирует, к концу гаснет
                    root.rotation = Quaternion.identity;
                    var sc = Vector3.one * Mathf.Max(0.01f, f.R * 2f);
                    parts[0].localScale = sc; parts[1].localScale = sc;
                    bool armed = f.Phase == 1;
                    float pulse = armed ? 0.75f + 0.25f * Mathf.Sin(time * 14f) : 1f;
                    float fade = Mathf.Clamp01(f.Life * 2f);
                    owned[0].SetColor("_BaseColor", (armed ? new Color(0.85f, 0.6f, 1f, 1f) : new Color(0.55f, 0.95f, 0.9f, 0.85f)) * new Color(1f, 1f, 1f, pulse * fade));
                    owned[1].SetColor("_BaseColor", (armed ? new Color(0.6f, 0.35f, 0.9f, 0.3f) : new Color(0.35f, 0.75f, 0.75f, 0.18f)) * new Color(1f, 1f, 1f, fade));
                    break;
                }
                case EffectKind.EnemyBolt:
                    parts[0].localScale = Vector3.one * (0.32f + 0.05f * Mathf.Sin(time * 30f));
                    break;
                case EffectKind.FireTrail:
                    SyncTrail(f, time);
                    root.position = Vector3.zero; root.rotation = Quaternion.identity;
                    break;
                case EffectKind.Tether:
                    root.position = Vector3.zero;
                    line.SetPosition(0, Art.W(sim.Player.Pos, 0.7f));
                    line.SetPosition(1, Art.W(f.Pos, 0.9f));
                    break;
                default:
                    if (f.Kind >= EffectKind.FormShot) SyncForm(f, sim, time);
                    break;
            }
        }

        /// <summary>Синий огонь вдоль точек следа: язычки появляются, пока точки свежие.</summary>
        void SyncTrail(Effect f, float time)
        {
            var pts = f.Points;
            var flameMat = Art.LitMat(new Color(0.35f, 0.75f, 1f), 3f);
            // заполняем отрезки язычками через 0.45 юнита
            while (builtPoints < pts.Count)
            {
                int i = builtPoints++;
                var a = pts[i];
                var b = i + 1 < pts.Count ? pts[i + 1] : a;
                int n = Mathf.Max(1, Mathf.CeilToInt((b - a).magnitude / 0.45f));
                for (int k = 0; k < n; k++)
                {
                    var p = Vector2.Lerp(a, b, (float)k / n);
                    var go = Art.Part(root, MeshKit.Prism(4, 0f), flameMat, Art.W(p), new Vector3(0.3f, 0.5f, 0.3f), new Vector3(0f, k * 37f, 0f), false);
                    go.name = i.ToString();
                    parts.Add(go.transform);
                }
            }
            // гасим язычки старых точек
            for (int j = parts.Count - 1; j >= 0; j--)
            {
                var tr = parts[j];
                bool alive = false;
                foreach (var p in pts) if ((Art.W(p) - tr.position).sqrMagnitude < 0.6f) { alive = true; break; }
                if (!alive) { Object.Destroy(tr.gameObject); parts.RemoveAt(j); builtPoints = Mathf.Min(builtPoints, pts.Count); continue; }
                float h = 0.4f + 0.15f * Mathf.Sin(time * 18f + j);
                tr.localScale = new Vector3(0.3f, h, 0.3f);
            }
        }

        public void Destroy()
        {
            foreach (var m in owned) Object.Destroy(m);
            Object.Destroy(root.gameObject);
        }
    }
}
