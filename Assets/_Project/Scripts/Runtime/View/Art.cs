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
        /// <summary>Бирюза души: глаза Смерти, карты без свойств.</summary>
        public static readonly Color Soul = new(0.35f, 0.95f, 0.85f);

        public static readonly Color Pink = new(0.96f, 0.04f, 0.95f);
        public static readonly Color Yellow = new(0.96f, 0.85f, 0.04f);
        public static readonly Color Blue = new(0.04f, 0.84f, 0.96f);
        public static readonly Color Danger = new(1f, 0.22f, 0.12f);
        public static readonly Color DangerEdge = new(1f, 0.22f, 0.14f);

        static readonly Dictionary<(Color, float, bool), Material> Lit = new();
        static readonly Dictionary<(Color, int), Material> Unlit = new();
        static Shader litShader, unlitShader;

        /// <summary>Образцы материалов в Resources/ShaderKeep (создаёт BuildTools.EnsureShaderKeep): Lit и Unlit, непрозрачные
        /// и прозрачные. Все материалы игры — их копии: на образцы ссылается сборка, поэтому шейдеры и ровно эти варианты
        /// (ключевые слова, которые URP выставил образцам при импорте) попадают в билд. Без них в билде чёрный экран.</summary>
        public static readonly string[] KeepNames = { "Lit", "LitTransparent", "Unlit", "UnlitTransparent" };

        static Material[] keep;

        /// <summary>Новый материал: копия образца (lit — освещаемый, transparent — прозрачный). Пока образцов нет
        /// (до первой сборки) — собирается по тем же правилам из шейдера.</summary>
        static Material Base(bool lit, bool transparent)
        {
            keep ??= System.Array.ConvertAll(KeepNames, n => Resources.Load<Material>("ShaderKeep/" + n));
            var k = keep[(lit ? 0 : 2) + (transparent ? 1 : 0)];
            if (k != null) return new Material(k);
            var m = new Material(lit ? LitShader : UnlitShader);
            if (transparent) MakeTransparent(m, 3000);
            return m;
        }

        public static Shader LitShader => litShader ??= Shader.Find("Universal Render Pipeline/Lit");
        public static Shader UnlitShader => unlitShader ??= Shader.Find("Universal Render Pipeline/Unlit");
        static Texture2D disc, ring, rect, softDisc;
        static readonly Texture2D[] attackIcons = new Texture2D[4];

        /// <summary>Освещаемый материал цвета c. Со свечением (emission > 0) — неосвещаемый, цвет × (0.5 + emission) уходит
        /// выше 1 и светится через bloom: так не нужен отдельный вариант шейдера с _EMISSION.</summary>
        public static Material LitMat(Color c, float emission = 0f, bool transparent = false)
        {
            var key = (c, emission, transparent);
            if (Lit.TryGetValue(key, out var m) && m != null) return m;
            if (emission > 0f)
            {
                m = Base(false, transparent);
                m.name = "Glow";
                m.SetColor("_BaseColor", Glow(c, emission));
            }
            else
            {
                m = Base(true, transparent);
                m.name = "Lit";
                m.SetColor("_BaseColor", c);
                m.SetFloat("_Smoothness", 0.12f);
                m.SetFloat("_Metallic", 0f);
            }
            if (transparent) m.renderQueue = 3000;
            return Lit[key] = m;
        }

        /// <summary>Цвет свечения для неосвещаемого материала: выше 1, светится через bloom.</summary>
        public static Color Glow(Color c, float emission)
        {
            float k = 0.5f + emission;
            return new Color(c.r * k, c.g * k, c.b * k, c.a);
        }

        /// <summary>Неосвещённый прозрачный материал для декалей на полу. tex: 0 — круг, 1 — кольцо, 2 — рамка, 3 — мягкий круг, -1 — без текстуры.</summary>
        public static Material DecalMat(Color c, int tex, int queueOffset = 0)
        {
            var key = (c, tex * 100 + queueOffset);
            if (Unlit.TryGetValue(key, out var m) && m != null) return m;
            m = Base(false, true);
            m.name = "Decal";
            m.SetColor("_BaseColor", c);
            var t = tex switch { 0 => Disc(), 1 => Ring(), 2 => Rect(), 3 => SoftDisc(), _ => null };
            m.SetTexture("_BaseMap", t);
            m.renderQueue = 3000 + queueOffset;
            return Unlit[key] = m;
        }

        /// <summary>Свой экземпляр декали — для изменения цвета/прозрачности по кадрам.</summary>
        public static Material DecalInstance(Color c, int tex, int queueOffset = 0) => new(DecalMat(c, tex, queueOffset));

        public static void MakeTransparent(Material m, int queue)
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

        /// <summary>Значок атаки над врагом — по форме её зоны: удар с выпадом (трапеция) — стрелка вперёд, размах (сектор) — дуга,
        /// выстрел (полоса) — прицел, удар по площади (круг) — взрыв.</summary>
        public static Texture2D AttackIcon(Arena.Sim.TeleShape shape) => attackIcons[(int)shape] ??= shape switch
        {
            Arena.Sim.TeleShape.Trapezoid => MakeTex((x, y) =>
            {
                float shaft = Edge(Mathf.Abs(x), 0.17f, 0.04f) * Edge(y, 0.2f, 0.04f) * Edge(-y, 0.8f, 0.04f);
                float head = Edge(Mathf.Abs(x) - (0.88f - y) * 0.95f, 0f, 0.05f) * Edge(-y, -0.02f, 0.04f) * Edge(y, 0.88f, 0.04f);
                return Mathf.Max(shaft, head);
            }),
            Arena.Sim.TeleShape.Fan => MakeTex((x, y) =>
            {
                float r = Mathf.Sqrt(x * x + y * y), a = Mathf.Abs(Mathf.Atan2(x, y)) * Mathf.Rad2Deg;
                return Edge(Mathf.Abs(r - 0.68f), 0.16f, 0.04f) * Edge(a, 80f, 6f);
            }),
            Arena.Sim.TeleShape.Lane => MakeTex((x, y) =>
            {
                float r = Mathf.Sqrt(x * x + y * y);
                float ring = Edge(Mathf.Abs(r - 0.62f), 0.1f, 0.04f), dot = Edge(r, 0.17f, 0.04f);
                float ticks = Mathf.Max(Edge(Mathf.Abs(x), 0.07f, 0.03f) * Edge(-Mathf.Abs(y), -0.45f, 0.03f) * Edge(Mathf.Abs(y), 0.95f, 0.03f),
                                        Edge(Mathf.Abs(y), 0.07f, 0.03f) * Edge(-Mathf.Abs(x), -0.45f, 0.03f) * Edge(Mathf.Abs(x), 0.95f, 0.03f));
                return Mathf.Max(ring, Mathf.Max(dot, ticks));
            }),
            _ => MakeTex((x, y) =>
            {
                float r = Mathf.Sqrt(x * x + y * y), a = Mathf.Atan2(y, x);
                float spike = Mathf.Pow(Mathf.Max(0f, Mathf.Cos(a * 8f)), 3f);
                return Edge(r, 0.36f + 0.5f * spike, 0.05f);
            }),
        };

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
            Arena.Core.CardColor.Staff => Soul,
            _ => Blue,
        };

        public static Vector3 W(Vector2 p, float y = 0f) => new(p.x, y, p.y);

        /// <summary>Милость — мягкий малахит: ромбики на полу, счётчик в HUD, цены в магазине.</summary>
        public static readonly Color Grace = new(0.3f, 0.78f, 0.58f);
        /// <summary>Сардис — мета-валюта: тёмно-красный камень (Откр. 4:3).</summary>
        public static readonly Color Sardis = new(0.72f, 0.1f, 0.16f);
        /// <summary>Скиллы элиток — красные, как телеграфы врагов.</summary>
        public static readonly Color EliteRed = new(1f, 0.28f, 0.22f);

        /// <summary>Цвет свойства формы — им светятся снаряды, зоны, духи и подписи.</summary>
        public static Color TraitColor(Arena.Core.Trait t) => t switch
        {
            Arena.Core.Trait.Fire => new Color(1f, 0.55f, 0.15f),
            Arena.Core.Trait.Frost => new Color(0.55f, 0.85f, 1f),
            Arena.Core.Trait.Lightning => new Color(1f, 0.95f, 0.45f),
            Arena.Core.Trait.Rot => new Color(0.55f, 0.92f, 0.3f),
            Arena.Core.Trait.Doom => new Color(0.78f, 0.42f, 1f),
            Arena.Core.Trait.Ricochet => new Color(0.92f, 0.92f, 0.95f),
            Arena.Core.Trait.Split => new Color(0.4f, 1f, 0.8f),
            Arena.Core.Trait.Pierce => new Color(1f, 0.38f, 0.38f),
            Arena.Core.Trait.Return => new Color(0.45f, 0.7f, 1f),
            Arena.Core.Trait.Echo => new Color(0.72f, 0.76f, 1f),
            Arena.Core.Trait.Pull => new Color(0.55f, 0.35f, 1f),
            Arena.Core.Trait.Push => new Color(1f, 0.82f, 0.35f),
            Arena.Core.Trait.Blast => new Color(1f, 0.42f, 0.15f),
            Arena.Core.Trait.Reap => new Color(0.35f, 0.95f, 0.85f),
            Arena.Core.Trait.Homing => new Color(1f, 0.55f, 0.75f),
            Arena.Core.Trait.Orbit => new Color(0.6f, 0.9f, 1f),
            Arena.Core.Trait.Trigger => new Color(1f, 0.7f, 0.95f),
            Arena.Core.Trait.Kinetic => new Color(1f, 0.62f, 0.4f),
            Arena.Core.Trait.Snipe => new Color(0.75f, 0.95f, 1f),
            Arena.Core.Trait.Siphon => new Color(0.95f, 0.3f, 0.45f),
            _ => new Color(0.85f, 1f, 0.4f),
        };

        /// <summary>Цвет карты: её первого свойства, без свойств — холодная душа.</summary>
        /// <summary>Цвет тринкета: тринкет свойства — цвет свойства, формы — золото, механики — цвет души.</summary>
        public static Color TrinketColor(Arena.Core.TrinketKind k) =>
            Arena.Core.TrinketBook.TraitOf(k) is Arena.Core.Trait t ? TraitColor(t)
            : Arena.Core.TrinketBook.FormOf(k) != null ? new Color(1f, 0.8f, 0.4f)
            : Arena.Core.TrinketBook.Group(k) == Arena.Core.TrinketGroup.Curse ? new Color(0.85f, 0.2f, 0.3f) : Soul;

        /// <summary>Цвет редкости: редкая — синяя, эпическая — фиолетовая, легендарная — золотая.</summary>
        public static Color RarityColor(Arena.Core.Rarity r) => r switch
        {
            Arena.Core.Rarity.Rare => new Color(0.4f, 0.68f, 1f),
            Arena.Core.Rarity.Epic => new Color(0.74f, 0.45f, 1f),
            _ => new Color(1f, 0.76f, 0.25f),
        };

        public static Color CardTint(Arena.Core.SkillCard c) => c == null ? Soul : c.Own.HasValue ? TraitColor(c.Own.Value) : c.Souls.Count > 0 ? TraitColor(c.Souls[0]) : Soul;
    }
}
