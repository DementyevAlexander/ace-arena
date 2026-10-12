using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace Arena.View
{
    /// <summary>Материалы URP из кода и сборка фигур из low-poly частей. Палитра — мягкая, как в TUNIC.</summary>
    public static class Art
    {
        public static readonly Color Grass = new(0.47f, 0.70f, 0.38f);
        public static readonly Color GrassDark = new(0.33f, 0.56f, 0.32f);
        public static readonly Color Stone = new(0.80f, 0.76f, 0.68f);
        public static readonly Color StoneDark = new(0.62f, 0.58f, 0.54f);
        public static readonly Color Moss = new(0.42f, 0.62f, 0.34f);
        public static readonly Color Foliage = new(0.30f, 0.62f, 0.52f);
        public static readonly Color FoliageWarm = new(0.95f, 0.62f, 0.32f);
        public static readonly Color Trunk = new(0.52f, 0.38f, 0.30f);
        /// <summary>Ночной город в духе Hades: тёмный камень, тусклая штукатурка, бордовые крыши, тёплый огонь жаровен.</summary>
        public static class Night
        {
            public static readonly Color Sky = new(0.035f, 0.025f, 0.045f);
            public static readonly Color Ground = new(0.075f, 0.065f, 0.075f);
            public static readonly Color[] Flags = { new(0.30f, 0.27f, 0.28f), new(0.23f, 0.21f, 0.23f), new(0.35f, 0.30f, 0.28f), new(0.31f, 0.22f, 0.22f) };
            public static readonly Color Plinth = new(0.16f, 0.14f, 0.16f);
            public static readonly Color Plaster = new(0.42f, 0.36f, 0.34f);
            public static readonly Color PlasterDark = new(0.30f, 0.26f, 0.27f);
            public static readonly Color Roof = new(0.38f, 0.14f, 0.13f);
            public static readonly Color RoofDark = new(0.24f, 0.10f, 0.12f);
            public static readonly Color Masonry = new(0.36f, 0.33f, 0.34f);
            public static readonly Color Marble = new(0.62f, 0.58f, 0.57f);
            public static readonly Color Wood = new(0.30f, 0.20f, 0.15f);
            public static readonly Color Terracotta = new(0.55f, 0.27f, 0.18f);
            public static readonly Color DeadWood = new(0.18f, 0.14f, 0.14f);
            public static readonly Color Abyss = new(0.03f, 0.015f, 0.05f);
            public static readonly Color AbyssGlow = new(0.35f, 0.12f, 0.55f);
            public static readonly Color Fire = new(1f, 0.52f, 0.18f);
            public static readonly Color Window = new(1f, 0.62f, 0.28f);
            public static readonly Color Iron = new(0.13f, 0.12f, 0.13f);
            public static readonly Color Soul = new(0.35f, 0.95f, 0.85f);
        }

        public static readonly Color Pink = new(0.96f, 0.04f, 0.95f);
        public static readonly Color Yellow = new(0.96f, 0.85f, 0.04f);
        public static readonly Color Blue = new(0.04f, 0.84f, 0.96f);
        public static readonly Color Danger = new(1f, 0.22f, 0.12f);
        public static readonly Color DangerEdge = new(1f, 0.55f, 0.15f);

        static readonly Dictionary<(Color, float, bool), Material> Lit = new();
        static readonly Dictionary<(Color, int), Material> Unlit = new();
        static Shader litShader, unlitShader;
        static Texture2D disc, ring, rect, softDisc;

        public static Material LitMat(Color c, float emission = 0f, bool transparent = false)
        {
            var key = (c, emission, transparent);
            if (Lit.TryGetValue(key, out var m) && m != null) return m;
            litShader ??= Shader.Find("Universal Render Pipeline/Lit");
            m = new Material(litShader) { name = "Lit" };
            m.SetColor("_BaseColor", c);
            m.SetFloat("_Smoothness", 0.12f);
            m.SetFloat("_Metallic", 0f);
            if (emission > 0f)
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", c * emission);
                m.globalIlluminationFlags = MaterialGlobalIlluminationFlags.None;
            }
            if (transparent) MakeTransparent(m, 3000);
            return Lit[key] = m;
        }

        /// <summary>Неосвещённый прозрачный материал для декалей на полу. tex: 0 — круг, 1 — кольцо, 2 — рамка, 3 — мягкий круг, -1 — без текстуры.</summary>
        public static Material DecalMat(Color c, int tex, int queueOffset = 0)
        {
            var key = (c, tex * 100 + queueOffset);
            if (Unlit.TryGetValue(key, out var m) && m != null) return m;
            unlitShader ??= Shader.Find("Universal Render Pipeline/Unlit");
            m = new Material(unlitShader) { name = "Decal" };
            m.SetColor("_BaseColor", c);
            var t = tex switch { 0 => Disc(), 1 => Ring(), 2 => Rect(), 3 => SoftDisc(), _ => null };
            if (t != null) m.SetTexture("_BaseMap", t);
            MakeTransparent(m, 3000 + queueOffset);
            return Unlit[key] = m;
        }

        /// <summary>Свой экземпляр декали — для изменения цвета/прозрачности по кадрам.</summary>
        public static Material DecalInstance(Color c, int tex, int queueOffset = 0) => new(DecalMat(c, tex, queueOffset));

        static void MakeTransparent(Material m, int queue)
        {
            m.SetFloat("_Surface", 1f);
            m.SetFloat("_Blend", 0f);
            m.SetOverrideTag("RenderType", "Transparent");
            m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
            m.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
            m.SetFloat("_ZWrite", 0f);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = queue;
        }

        public static Texture2D Disc() => disc ??= MakeTex((x, y) => Edge(Mathf.Sqrt(x * x + y * y), 1f, 0.02f));
        public static Texture2D SoftDisc() => softDisc ??= MakeTex((x, y) => Mathf.Clamp01(1f - Mathf.Sqrt(x * x + y * y)) * Mathf.Clamp01(1f - Mathf.Sqrt(x * x + y * y)));
        public static Texture2D Ring() => ring ??= MakeTex((x, y) =>
        {
            float r = Mathf.Sqrt(x * x + y * y);
            return Edge(r, 1f, 0.02f) * (1f - Edge(r, 0.86f, 0.02f));
        });
        public static Texture2D Rect() => rect ??= MakeTex((x, y) =>
        {
            float d = Mathf.Max(Mathf.Abs(x), Mathf.Abs(y));
            return Edge(d, 1f, 0.02f) * (1f - Edge(d, 0.84f, 0.02f));
        });

        static float Edge(float v, float limit, float soft) => Mathf.Clamp01((limit - v) / soft);

        static Texture2D MakeTex(System.Func<float, float, float> alpha)
        {
            const int n = 128;
            var t = new Texture2D(n, n, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color32[n * n];
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f;
                px[y * n + x] = new Color(1f, 1f, 1f, alpha(u, v));
            }
            t.SetPixels32(px);
            t.Apply();
            return t;
        }

        // ---------- Сборка фигур ----------

        public static GameObject Part(Transform parent, Mesh mesh, Material mat, Vector3 pos, Vector3 scale, Vector3 euler = default, bool shadows = true)
        {
            var go = new GameObject(mesh.name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = scale;
            go.transform.localEulerAngles = euler;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = mat;
            r.shadowCastingMode = shadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            r.receiveShadows = true;
            return go;
        }

        /// <summary>Верх самых высоких плит пола: декали кладутся выше, иначе плитки их перекрывают.</summary>
        public const float FloorTop = 0.07f;

        public static GameObject Decal(Transform parent, Mesh mesh, Material mat, float y = 0.02f)
        {
            var go = Part(parent, mesh, mat, new Vector3(0f, FloorTop + y, 0f), Vector3.one, default, false);
            go.GetComponent<MeshRenderer>().receiveShadows = false;
            return go;
        }

        public static Color CardColor(Arena.Core.CardColor c) => c switch
        {
            Arena.Core.CardColor.Pink => Pink,
            Arena.Core.CardColor.Yellow => Yellow,
            _ => Blue,
        };

        public static Vector3 W(Vector2 p, float y = 0f) => new(p.x, y, p.y);
    }
}
