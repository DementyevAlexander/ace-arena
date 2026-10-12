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

            for (int i = flashes.Count - 1; i >= 0; i--)
            {
                var fl = flashes[i];
                fl.T += dt;
                float k = fl.T / fl.Life;
                if (k >= 1f) { Object.Destroy(fl.Go); Object.Destroy(fl.Mat); flashes.RemoveAt(i); continue; }
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
            var mat = Art.DecalInstance(color, 1, 6);
            var go = Art.Decal(root, MeshKit.FloorQuad(), mat, 0.05f);
            go.transform.position = Art.W(pos, Art.FloorTop + 0.05f);
            flashes.Add(new Flash { Go = go, Mat = mat, Color = color, Life = life, From = Vector3.one * radius * 1.2f, To = Vector3.one * radius * 2.1f });
            var mat2 = Art.DecalInstance(new Color(color.r, color.g, color.b, color.a * 0.45f), 0, 5);
            var go2 = Art.Decal(root, MeshKit.FloorQuad(), mat2, 0.045f);
            go2.transform.position = Art.W(pos, Art.FloorTop + 0.045f);
            flashes.Add(new Flash { Go = go2, Mat = mat2, Color = mat2.GetColor("_BaseColor"), Life = life * 0.8f, From = Vector3.one * radius * 1.6f, To = Vector3.one * radius * 2f });
        }

        /// <summary>Вспышка-сфера (попадание, телепорт).</summary>
        public void Puff(Vector2 pos, float size, Color color, float life = 0.25f, float y = 0.6f)
        {
            var mat = new Material(Art.LitMat(color, 2f, true));
            var go = Art.Part(root, MeshKit.Ico(1), mat, Art.W(pos, y), Vector3.one * size, default, false);
            flashes.Add(new Flash { Go = go, Mat = mat, Color = color, Life = life, From = Vector3.one * size * 0.5f, To = Vector3.one * size * 1.3f });
        }

        sealed class Flash
        {
            public GameObject Go;
            public Material Mat;
            public Color Color;
            public float T, Life;
            public Vector3 From, To;
        }
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
                case EffectKind.Barrel:
                {
                    // бочка и её круг падения: контур сразу, заливка растёт к моменту взрыва
                    Add(Art.Part(root, MeshKit.Prism(10, 0.9f), Art.LitMat(Art.Night.Wood), Vector3.zero, new Vector3(0.45f, 0.55f, 0.45f), new Vector3(70f, 0f, 0f)));
                    var om = Art.DecalInstance(Art.DangerEdge, 1, 2); owned.Add(om);
                    var o = Art.Decal(root, MeshKit.FloorQuad(), om, 0.03f); o.transform.localScale = Vector3.one * f.R * 2f; Add(o);
                    var fm = Art.DecalInstance(Art.Danger, 0, 3); owned.Add(fm);
                    Add(Art.Decal(root, MeshKit.FloorQuad(), fm, 0.035f));
                    var sm = Art.DecalInstance(new Color(0f, 0f, 0f, 0.45f), 3, 1); owned.Add(sm);
                    var sh = Art.Decal(root, MeshKit.FloorQuad(), sm, 0.04f); sh.transform.localScale = Vector3.one * 0.6f; Add(sh);
                    break;
                }
                case EffectKind.EnemyFire:
                {
                    var m = Art.DecalInstance(new Color(1f, 0.42f, 0.12f, 0.55f), 3, 4); owned.Add(m);
                    var d = Art.Decal(root, MeshKit.FloorQuad(), m, 0.04f); d.transform.localScale = Vector3.one * f.R * 2.3f; Add(d);
                    var flame = Art.LitMat(Art.Night.Fire, 2f);
                    var rnd = new System.Random(f.Id);
                    for (int i = 0; i < 7; i++)
                    {
                        float a = (float)rnd.NextDouble() * Mathf.PI * 2f, rr = Mathf.Sqrt((float)rnd.NextDouble()) * f.R * 0.8f;
                        Add(Art.Part(root, MeshKit.Prism(5, 0f), flame, new Vector3(Mathf.Cos(a) * rr, 0f, Mathf.Sin(a) * rr), new Vector3(0.3f, 0.6f, 0.3f), default, false));
                    }
                    break;
                }
                case EffectKind.Tether:
                    line = root.gameObject.AddComponent<LineRenderer>();
                    line.sharedMaterial = Art.LitMat(new Color(1f, 0.92f, 0.5f), 3f);
                    line.widthMultiplier = 0.12f; line.positionCount = 2;
                    line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
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
                case EffectKind.Barrel:
                {
                    // низкая дуга (LaunchAngle 15°): вершина ≈ d·tg15° / 4, но не ниже 0.6 — бочку должно быть видно
                    float k = 1f - Mathf.Clamp01(f.T / Mathf.Max(f.Life, 0.0001f));
                    float d = (f.To - f.Start).magnitude, h = Mathf.Max(0.6f, d * 0.067f) * 4f * k * (1f - k) + 0.3f;
                    root.position = Art.W(f.Pos); root.rotation = Quaternion.identity;
                    parts[0].localPosition = Vector3.up * h;
                    parts[0].localEulerAngles = new Vector3(70f + time * 400f, 0f, 0f);
                    var at = Art.W(f.To, Art.FloorTop);
                    parts[1].position = at + Vector3.up * 0.03f; parts[2].position = at + Vector3.up * 0.035f;
                    float p = f.Hold + (1f - f.Hold) * k;
                    parts[2].localScale = Vector3.one * (f.R * 2f * p);
                    float pulse = p > 0.75f ? (Mathf.Repeat(time * 14f, 1f) < 0.5f ? 1f : 0.65f) : 1f;
                    owned[0].SetColor("_BaseColor", new Color(1f, 0.6f, 0.15f, (0.75f + 0.25f * p) * pulse));
                    owned[1].SetColor("_BaseColor", new Color(1f, 0.15f, 0.08f, (0.45f + 0.4f * p) * pulse));
                    parts[3].localPosition = new Vector3(0f, Art.FloorTop + 0.04f, 0f);
                    break;
                }
                case EffectKind.EnemyFire:
                {
                    float fade = Mathf.Clamp01(f.Life) * Mathf.Clamp01(f.Age * 4f);
                    root.localScale = Vector3.one * (0.4f + 0.6f * fade);
                    for (int i = 1; i < parts.Count; i++)
                        parts[i].localScale = new Vector3(0.3f, 0.45f + 0.35f * Mathf.PerlinNoise(time * 6f, i * 1.7f + f.Id), 0.3f);
                    break;
                }
                case EffectKind.FireTrail:
                    SyncTrail(f, time);
                    root.position = Vector3.zero; root.rotation = Quaternion.identity;
                    break;
                case EffectKind.Tether:
                    root.position = Vector3.zero;
                    line.SetPosition(0, Art.W(sim.Player.Pos, 0.7f));
                    line.SetPosition(1, Art.W(f.Pos, 0.9f));
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
