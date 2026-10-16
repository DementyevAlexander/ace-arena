using System.Collections.Generic;
using Arena.Core;
using Arena.Sim;
using Arena.UI;
using UnityEngine;

namespace Arena.View
{
    /// <summary>Карточки пятачка наград в конце комнаты: светящиеся карты цвета свойства вырастают из пола
    /// (ArenaTuning.lootAppear) и парят над кольцами — покачиваются и поворачиваются. Кольцо — цвета редкости,
    /// у ближайшей к Смерти ярче. Плиты под ними нет. Тринкет (мини-босс) — кристалл своего цвета над золотым кольцом.</summary>
    public sealed class LootView
    {
        /// <summary>Высота карточки над полом и её верх (с покачиванием) — над ним HUD пишет название.</summary>
        public const float CardY = 1.1f, CardTop = CardY + 0.35f + 0.12f;

        readonly Transform root;
        readonly List<CardView> cards = new();

        sealed class CardView
        {
            public Transform Root, Card, Gem;
            public Material CardMat, RingMat, GlowMat;
            public Light Light;
            public object Shown;
        }

        public LootView(Transform parent)
        {
            root = new GameObject("Loot").transform;
            root.SetParent(parent, false);
        }

        CardView MakeCard()
        {
            var c = new CardView { Root = new GameObject("Card").transform };
            c.Root.SetParent(root, false);
            c.CardMat = new Material(Art.LitMat(Color.white, 1.4f));
            c.Card = Art.Part(c.Root, MeshKit.Box(), c.CardMat, new Vector3(0f, CardY, 0f), new Vector3(0.5f, 0.7f, 0.05f), default, true).transform;
            c.Gem = Art.Part(c.Root, MeshKit.Ico(1), c.CardMat, new Vector3(0f, CardY, 0f), Vector3.one * 0.5f, default, true).transform;
            c.RingMat = Art.DecalInstance(Color.white, 1, 6);
            var ring = Art.Decal(c.Root, MeshKit.FloorQuad(), c.RingMat, 0.05f).transform;
            ring.localScale = Vector3.one * 1.6f;
            c.GlowMat = Art.DecalInstance(Color.white, 3, 5);
            var glow = Art.Decal(c.Root, MeshKit.FloorQuad(), c.GlowMat, 0.04f).transform;
            glow.localScale = Vector3.one * 2.4f;
            c.Light = new GameObject("Light").AddComponent<Light>();
            c.Light.transform.SetParent(c.Root, false);
            c.Light.transform.localPosition = new Vector3(0f, 1.2f, 0f);
            c.Light.type = LightType.Point;
            c.Light.range = 3.5f;
            c.Light.intensity = 2.5f;
            c.Light.shadows = LightShadows.None;
            cards.Add(c);
            return c;
        }

        public void Sync(ArenaSim sim, float time)
        {
            var near = sim.LootNear;
            float appear = Mathf.Max(0.01f, sim.ArenaCfg.lootAppear);
            float k = Mathf.Clamp01((sim.Time - (sim.LootReadyAt - appear)) / appear);
            float grow = 1f - (1f - k) * (1f - k); // замедляется к концу
            while (cards.Count < sim.Loot.Count) MakeCard();
            for (int i = 0; i < cards.Count; i++)
            {
                var c = cards[i];
                bool show = i < sim.Loot.Count && k > 0f;
                if (c.Root.gameObject.activeSelf != show) c.Root.gameObject.SetActive(show);
                if (!show) continue;
                var g = sim.Loot[i];
                bool gem = g.Trinket != null;
                var col = gem ? Art.TrinketColor(g.Trinket.Kind) : Art.CardTint(g.Card);
                object item = gem ? g.Trinket : g.Card;
                if (item != c.Shown)
                {
                    c.Shown = item;
                    c.CardMat.SetColor("_BaseColor", Art.Glow(col, 1.4f));
                    c.Light.color = col;
                    c.Card.gameObject.SetActive(!gem);
                    c.Gem.gameObject.SetActive(gem);
                }
                bool isNear = g == near;
                var rar = gem ? new Color(1f, 0.76f, 0.25f) : Art.RarityColor(g.Card.Rarity);
                c.RingMat.SetColor("_BaseColor", rar.WithAlpha((isNear ? 0.95f : 0.55f + 0.2f * Mathf.Sin(time * 3f + i)) * k));
                c.GlowMat.SetColor("_BaseColor", col.WithAlpha((isNear ? 0.45f : 0.25f) * k));
                c.Root.position = Art.W(g.Pos);
                var body = gem ? c.Gem : c.Card;
                body.localPosition = new Vector3(0f, CardY * grow + 0.12f * Mathf.Sin(time * 2.2f + i * 1.7f) * k, 0f);
                body.localScale = (gem ? Vector3.one * 0.5f : new Vector3(0.5f, 0.7f, 0.05f)) * grow;
                body.localEulerAngles = gem ? new Vector3(20f, time * 50f + i * 90f, 10f) : new Vector3(0f, time * 70f + i * 90f, 0f);
                c.Light.intensity = 2.5f * k;
            }
        }
    }
}
