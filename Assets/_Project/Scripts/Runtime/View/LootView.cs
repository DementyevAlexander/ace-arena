using Arena.Core;
using Arena.Sim;
using Arena.UI;
using UnityEngine;

namespace Arena.View
{
    /// <summary>Карточка на земле в конце комнаты, как дар в Hades: светящаяся карта цвета свойства парит над кольцом,
    /// покачивается и поворачивается; рядом с ней кольцо ярче — можно подобрать.</summary>
    public sealed class LootView
    {
        readonly Transform root, card;
        readonly Material cardMat, ringMat, glowMat;
        readonly Light light;
        SkillCard shown;

        public LootView(Transform parent)
        {
            root = new GameObject("Loot").transform;
            root.SetParent(parent, false);
            cardMat = new Material(Art.LitMat(Color.white, 1.4f));
            card = Art.Part(root, MeshKit.Box(), cardMat, new Vector3(0f, 1.1f, 0f), new Vector3(0.5f, 0.7f, 0.05f), default, true).transform;
            ringMat = Art.DecalInstance(Color.white, 1, 6);
            var ring = Art.Decal(root, MeshKit.FloorQuad(), ringMat, 0.05f).transform;
            ring.localScale = Vector3.one * 1.6f;
            glowMat = Art.DecalInstance(Color.white, 3, 5);
            var glow = Art.Decal(root, MeshKit.FloorQuad(), glowMat, 0.04f).transform;
            glow.localScale = Vector3.one * 2.4f;
            light = new GameObject("Light").AddComponent<Light>();
            light.transform.SetParent(root, false);
            light.transform.localPosition = new Vector3(0f, 1.2f, 0f);
            light.type = LightType.Point;
            light.range = 3.5f;
            light.intensity = 2.5f;
            light.shadows = LightShadows.None;
            root.gameObject.SetActive(false);
        }

        public void Sync(ArenaSim sim, float time)
        {
            bool on = sim.Loot != null && !sim.RewardPending;
            if (root.gameObject.activeSelf != on) root.gameObject.SetActive(on);
            if (!on) return;
            if (sim.Loot != shown)
            {
                shown = sim.Loot;
                var c = Art.CardTint(shown);
                cardMat.SetColor("_BaseColor", Art.Glow(c, 1.4f));
                light.color = c;
            }
            var col = Art.CardTint(shown);
            bool near = sim.LootInReach;
            ringMat.SetColor("_BaseColor", col.WithAlpha(near ? 0.95f : 0.55f + 0.2f * Mathf.Sin(time * 3f)));
            glowMat.SetColor("_BaseColor", col.WithAlpha(near ? 0.45f : 0.25f));
            root.position = Art.W(sim.LootPos);
            card.localPosition = new Vector3(0f, 1.1f + 0.12f * Mathf.Sin(time * 2.2f), 0f);
            card.localEulerAngles = new Vector3(0f, time * 70f, 0f);
        }
    }
}
