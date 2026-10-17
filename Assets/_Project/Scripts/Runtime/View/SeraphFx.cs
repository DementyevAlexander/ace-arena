using UnityEngine;

namespace Arena.View
{
    /// <summary>Эффекты Серафима: свет от сияющего клинка и лиловый дым из кадила. Дым — клубы, которые поднимаются
    /// от кадила, растут и тают (остаются в мире, а не идут за героем).</summary>
    public sealed class SeraphFx : MonoBehaviour
    {
        const int Puffs = 7;
        const float PuffLife = 2.4f, PuffRise = 0.7f;

        Transform smokeAnchor, smokeParent;
        readonly Transform[] puffs = new Transform[Puffs];
        readonly Material[] puffMats = new Material[Puffs];
        readonly Vector3[] puffFrom = new Vector3[Puffs];
        readonly float[] puffPrev = new float[Puffs];

        /// <summary>blade — середина клинка (пустышка SwordFlame модели), smoke — над крышкой кадила (CenserSmoke),
        /// world — куда класть клубы дыма.</summary>
        public void Init(Transform blade, Transform smoke, Transform world)
        {
            var lt = new GameObject("BladeLight").AddComponent<Light>();
            lt.transform.SetParent(blade, false);
            lt.type = LightType.Point; lt.color = new Color(1f, 0.9f, 0.72f); lt.range = 2.2f; lt.intensity = 0.6f; lt.shadows = LightShadows.None;
            lt.gameObject.AddComponent<Flicker>().Seed = 3.1f;

            smokeAnchor = smoke;
            smokeParent = world;
            var glow = new GameObject("CenserLight").AddComponent<Light>();
            glow.transform.SetParent(smoke, false);
            glow.type = LightType.Point; glow.color = new Color(1f, 0.55f, 0.25f); glow.range = 1.6f; glow.intensity = 0.7f; glow.shadows = LightShadows.None;
            glow.gameObject.AddComponent<Flicker>().Seed = 7.3f;
            var baseMat = Art.LitMat(new Color(0.78f, 0.66f, 0.92f, 1f), 0f, true); // лиловый, как на концепте
            for (int i = 0; i < Puffs; i++)
            {
                puffMats[i] = new Material(baseMat);
                var go = Art.Part(world, MeshKit.Ico(1, 0.12f, i), puffMats[i], Vector3.zero, Vector3.zero, default, false);
                go.name = "Smoke";
                puffs[i] = go.transform;
                puffPrev[i] = 1f;
            }
        }

        void Update()
        {
            float time = Time.time;
            if (smokeAnchor == null) return;
            for (int i = 0; i < Puffs; i++)
            {
                float t = Mathf.Repeat(time / PuffLife + (float)i / Puffs, 1f);
                if (t < puffPrev[i]) puffFrom[i] = smokeAnchor.position; // новый клуб — от кадила там, где оно сейчас
                puffPrev[i] = t;
                var p = puffs[i];
                float sway = Mathf.Sin(time * 1.3f + i * 2.1f) * 0.08f * t;
                p.position = puffFrom[i] + new Vector3(sway, PuffRise * t, sway * 0.6f);
                p.localScale = Vector3.one * Mathf.Lerp(0.06f, 0.24f, t);
                p.rotation = Quaternion.Euler(0f, time * 20f + i * 50f, 0f);
                var c = puffMats[i].GetColor("_BaseColor");
                c.a = 0.55f * Mathf.Clamp01(t / 0.12f) * (1f - t);
                puffMats[i].SetColor("_BaseColor", c);
            }
        }

        void OnDisable()
        {
            foreach (var p in puffs) if (p != null) p.gameObject.SetActive(false);
        }

        void OnEnable()
        {
            for (int i = 0; i < Puffs; i++)
            {
                if (puffs[i] == null) continue;
                puffs[i].gameObject.SetActive(true);
                puffPrev[i] = 1f;
            }
        }

        void OnDestroy()
        {
            foreach (var p in puffs) if (p != null) Destroy(p.gameObject);
            foreach (var m in puffMats) if (m != null) Destroy(m);
        }
    }
}
