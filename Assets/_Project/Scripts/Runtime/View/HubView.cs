using System.Collections.Generic;
using Arena.Core;
using Arena.Sim;
using Arena.UI;
using UnityEngine;

namespace Arena.View
{
    /// <summary>Хаб поверх комнаты (RoomView рисует площадь, колонны и постаменты): врата — перемычка между столбами и
    /// светящийся проход, персонажи — фигуры своего цвета на постаментах. Кольцо под ближайшим местом ярче.</summary>
    public sealed class HubView
    {
        public readonly Transform Root;
        readonly List<(HubSpot spot, Material ring)> rings = new();
        readonly Material portalMat;
        readonly Color gateColor;

        static readonly Vector2 Up = new(0.70710678f, 0.70710678f);

        public HubView(ArenaSim sim)
        {
            Root = new GameObject("Hub").transform;
            var pal = RoomView.For(sim.Room.biome);
            foreach (var s in sim.HubSpots)
            {
                var ringMat = Art.DecalInstance(s.Color, 1, 6);
                var ring = Art.Decal(Root, MeshKit.FloorQuad(), ringMat, 0.05f).transform;
                ring.position = Art.W(s.Pos, Art.FloorTop + 0.05f);
                ring.localScale = Vector3.one * (s.Kind == HubSpotKind.Gate ? 3.6f : 1.8f);
                rings.Add((s, ringMat));
                if (s.Kind == HubSpotKind.Gate) continue;
                // персонаж-заглушка: конус-плащ и голова своего цвета на постаменте
                var body = Art.LitMat(Color.Lerp(s.Color, pal.Wall, 0.45f));
                var head = Art.LitMat(Color.Lerp(s.Color, Color.white, 0.25f), 0.3f);
                var at = Art.W(s.Pos, 0.25f);
                Art.Part(Root, MeshKit.Prism(8, 0.45f), body, at, new Vector3(0.75f, 1.25f, 0.75f));
                Art.Part(Root, MeshKit.Ico(1), head, at + Vector3.up * 1.45f, Vector3.one * 0.42f);
            }

            // врата: перемычка над столбами и светящийся проход между ними (поперёк моста — вдоль горизонтали экрана)
            var gate = sim.HubSpots.Find(x => x.Kind == HubSpotKind.Gate);
            if (gate != null)
            {
                var c = gate.Pos + Up * 1f;
                var stone = Art.LitMat(pal.WallTop);
                Art.Part(Root, MeshKit.Box(), stone, Art.W(c, 3.3f), new Vector3(6.4f, 0.5f, 0.9f), new Vector3(0f, 45f, 0f));
                gateColor = gate.Color;
                portalMat = new Material(Art.LitMat(gate.Color, 2.2f));
                Art.Part(Root, MeshKit.Box(), portalMat, Art.W(c, 1.55f), new Vector3(4.4f, 3.0f, 0.12f), new Vector3(0f, 45f, 0f), false);
                var l = new GameObject("GateLight").AddComponent<Light>();
                l.transform.SetParent(Root, false);
                l.transform.position = Art.W(c - Up * 1.2f, 1.6f);
                l.type = LightType.Point; l.color = gate.Color; l.range = 7f; l.intensity = 3f; l.shadows = LightShadows.None;
            }
        }

        public void Sync(ArenaSim sim, float time)
        {
            var near = sim.HubNear;
            foreach (var (s, ring) in rings)
                ring.SetColor("_BaseColor", s.Color.WithAlpha(s == near ? 0.95f : 0.35f + 0.15f * Mathf.Sin(time * 2.5f)));
            if (portalMat != null) portalMat.SetColor("_BaseColor", Art.Glow(gateColor, 1.8f + 0.5f * Mathf.Sin(time * 2f)));
        }

        public void Destroy() => Object.Destroy(Root.gameObject);
    }
}
