using System.Collections.Generic;
using Arena.Core;
using Arena.Sim;
using Arena.View;
using UnityEngine;
using UnityEngine.UI;

namespace Arena.UI
{
    /// <summary>HUD: здоровье, волна, 4 слота карт с улучшениями и перезарядкой, цифры урона, баннеры.</summary>
    public sealed class Hud
    {
        static readonly string[] KeysMouse = { "ЛКМ", "ПКМ", "Q", "Space" };
        static readonly string[] KeysPad = { "X", "Y", "B", "A" };

        readonly Canvas canvas;
        readonly Image hpFill;
        readonly Text hpText, waveText, banner, buffText, hint;
        readonly SlotWidget[] slots = new SlotWidget[4];
        readonly RectTransform numbersLayer;
        readonly List<FloatNumber> numbers = new();
        readonly Image hurtFlash;
        float hurtFade = 0.3f;
        float bannerT;

        public Hud()
        {
            canvas = UiKit.Canvas("HUD", 10);
            var t = canvas.transform;
            hurtFlash = UiKit.Fill(t, "HurtFlash", new Color(1f, 0.1f, 0.1f, 0f));
            numbersLayer = UiKit.Stretch(t, "Numbers");

            UiKit.Label(t, "ACE", 30, new Color(1f, 0.85f, 0.45f), new Vector2(0f, 1f), new Vector2(40f, -36f), new Vector2(200f, 40f), TextAnchor.MiddleLeft).rectTransform.pivot = new Vector2(0f, 0.5f);
            var hpBg = UiKit.Image(t, "HpBg", new Color(0.12f, 0.05f, 0.16f, 0.9f), new Vector2(0f, 1f), new Vector2(40f, -64f), new Vector2(360f, 28f), new Vector2(0f, 1f));
            hpFill = UiKit.Image(hpBg.transform, "HpFill", new Color(1f, 0.32f, 0.45f), new Vector2(0f, 0.5f), new Vector2(3f, 0f), new Vector2(354f, 22f), new Vector2(0f, 0.5f));
            hpText = UiKit.Label(hpBg.transform, "", 18, Color.white, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(360f, 28f));
            buffText = UiKit.Label(t, "", 18, new Color(0.6f, 1f, 1f), new Vector2(0f, 1f), new Vector2(40f, -110f), new Vector2(600f, 26f), TextAnchor.MiddleLeft);
            buffText.rectTransform.pivot = new Vector2(0f, 0.5f);
            waveText = UiKit.Label(t, "", 26, Color.white, new Vector2(1f, 1f), new Vector2(-40f, -40f), new Vector2(500f, 40f), TextAnchor.MiddleRight);
            waveText.rectTransform.pivot = new Vector2(1f, 0.5f);
            hint = UiKit.Label(t, "", 16, new Color(0.85f, 0.85f, 0.95f, 0.85f), new Vector2(0f, 0f), new Vector2(40f, 30f), new Vector2(520f, 80f), TextAnchor.LowerLeft, FontStyle.Normal);
            hint.rectTransform.pivot = new Vector2(0f, 0f);
            banner = UiKit.Label(t, "", 64, new Color(1f, 0.88f, 0.4f), new Vector2(0.5f, 0.68f), Vector2.zero, new Vector2(1400f, 160f));

            for (int i = 0; i < 4; i++) slots[i] = new SlotWidget(t, new Vector2(-330f + i * 220f, 30f));
        }

        public void ShowBanner(string text, float time = 1.6f) { banner.text = text; bannerT = time; }

        /// <summary>Кровавая виньетка Hades: 0.3 с, а при сильном ударе (больше 10% здоровья) — 1 с.</summary>
        public void HurtFlash(bool major = false)
        {
            hurtFlash.color = new Color(1f, 0.1f, 0.1f, major ? 0.4f : 0.28f);
            hurtFade = major ? 1f : 0.3f;
        }

        public void Number(Vector2 world, float value, bool crit, Color color)
        {
            var txt = UiKit.Label(numbersLayer, Mathf.RoundToInt(value).ToString(), crit ? 36 : 26, color, Vector2.zero, Vector2.zero, new Vector2(160f, 50f));
            numbers.Add(new FloatNumber { Text = txt, World = new Vector3(world.x + Random.Range(-0.25f, 0.25f), 1.5f, world.y), T = 0f });
        }

        public void Sync(ArenaSim sim, Camera cam, bool useMouse, float dt, bool paused)
        {
            var p = sim.Player;
            float k = Mathf.Clamp01(p.Hp / sim.PlayerCfg.hp);
            hpFill.rectTransform.sizeDelta = new Vector2(354f * k, 22f);
            hpText.text = $"{Mathf.CeilToInt(p.Hp)} / {sim.PlayerCfg.hp:0}";
            waveText.text = $"Волна {sim.Wave}   ·   Убито {sim.Kills}";
            var buffs = new List<string>();
            if (p.Haste > 0f) buffs.Add($"Haste {p.Haste:0.0}с");
            if (p.BuffInvuln > 0f) buffs.Add($"Invulnerability {p.BuffInvuln:0.0}с");
            if (p.Shoes > 0f) buffs.Add($"Blazing Shoes {p.Shoes:0.0}с");
            if (p.Shield > 0f) buffs.Add("Aegis");
            buffText.text = string.Join("   ", buffs);
            hint.text = useMouse
                ? "WASD — бег · ЛКМ/ПКМ/Q/Space — карты · наведение мышью\nTab — колода · Esc — пауза · R — заново"
                : "Стик — бег и прицел · X/Y/B/A — карты · автолок как в Hades\nView — колода · Menu — пауза";
            var keys = useMouse ? KeysMouse : KeysPad;
            for (int i = 0; i < 4; i++) slots[i].Sync(sim, i, keys[i], sim.Player.Cast != null && sim.Player.Cast.Slot == i);

            if (bannerT > 0f)
            {
                bannerT -= dt;
                banner.color = banner.color.WithAlpha(Mathf.Clamp01(bannerT * 2f));
            }
            var hc = hurtFlash.color; hc.a = Mathf.Max(0f, hc.a - dt * 0.35f / Mathf.Max(0.1f, hurtFade)); hurtFlash.color = hc;

            var size = ((RectTransform)canvas.transform).sizeDelta;
            for (int i = numbers.Count - 1; i >= 0; i--)
            {
                var n = numbers[i];
                if (!paused) { n.T += dt; n.World.y += dt * 1.2f; }
                if (n.T > 0.8f || cam == null) { Object.Destroy(n.Text.gameObject); numbers.RemoveAt(i); continue; }
                var vp = cam.WorldToViewportPoint(n.World);
                n.Text.rectTransform.anchoredPosition = new Vector2(vp.x * size.x, vp.y * size.y);
                n.Text.color = n.Text.color.WithAlpha(Mathf.Clamp01((0.8f - n.T) * 3f));
            }
        }

        public void SetVisible(bool v) => canvas.enabled = v;

        sealed class FloatNumber
        {
            public Text Text;
            public Vector3 World;
            public float T;
        }

        /// <summary>Слот: главная карта, торчащая сбоку карта-улучшение, перезарядка, заряды, клавиша.</summary>
        sealed class SlotWidget
        {
            readonly RectTransform root;
            readonly CardFace face;
            readonly Image cdOverlay, upTab, highlight, upCd;
            readonly Text cdText, keyText, upText, react;
            readonly Image[] pips = new Image[5];

            public SlotWidget(Transform parent, Vector2 pos)
            {
                root = UiKit.Rect(parent, "Slot", new Vector2(0.5f, 0f), pos, new Vector2(170f, 230f), new Vector2(0.5f, 0f));
                upTab = UiKit.Image(root, "Up", new Color(0.18f, 0.1f, 0.26f), new Vector2(1f, 0.5f), new Vector2(-18f, 0f), new Vector2(64f, 170f), new Vector2(0f, 0.5f));
                upText = UiKit.Label(upTab.transform, "", 13, Color.white, new Vector2(0.5f, 0.5f), new Vector2(8f, 0f), new Vector2(150f, 40f));
                upText.rectTransform.localEulerAngles = new Vector3(0f, 0f, 90f);
                upCd = UiKit.Image(upTab.transform, "UpCd", new Color(0f, 0f, 0f, 0.6f), new Vector2(0.5f, 0f), Vector2.zero, new Vector2(64f, 0f), new Vector2(0.5f, 0f));
                highlight = UiKit.Image(root, "Highlight", new Color(1f, 1f, 1f, 0.9f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(182f, 242f));
                face = new CardFace(root, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(170f, 230f));
                cdOverlay = UiKit.Image(root, "Cd", new Color(0.02f, 0f, 0.05f, 0.7f), new Vector2(0.5f, 1f), Vector2.zero, new Vector2(170f, 0f), new Vector2(0.5f, 1f));
                cdText = UiKit.Label(root, "", 44, Color.white, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(170f, 60f));
                react = UiKit.Label(root, "повтор!", 24, new Color(1f, 0.6f, 0.95f), new Vector2(0.5f, 0.5f), new Vector2(0f, -10f), new Vector2(170f, 40f));
                for (int i = 0; i < pips.Length; i++)
                    pips[i] = UiKit.Image(root, "Pip", Color.white, new Vector2(1f, 1f), new Vector2(-16f - i * 18f, -44f), new Vector2(12f, 12f));
                var badge = UiKit.Image(root, "Key", new Color(1f, 0.85f, 0.45f), new Vector2(0f, 1f), new Vector2(-12f, 14f), new Vector2(70f, 34f), new Vector2(0f, 1f));
                keyText = UiKit.Label(badge.transform, "", 18, new Color(0.1f, 0.05f, 0.15f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(70f, 34f));
                keyText.GetComponent<Shadow>().enabled = false;
            }

            public void Sync(ArenaSim sim, int i, string key, bool casting)
            {
                var s = sim.Slots[i];
                face.Set(s.Main);
                keyText.text = key;
                highlight.enabled = casting;
                var card = s.Card;
                bool cooling = card != null && s.Charges <= 0 && s.Bat == null;
                float frac = cooling ? Mathf.Clamp01(s.CdTimer / card.Cooldown) : 0f;
                cdOverlay.rectTransform.sizeDelta = new Vector2(170f, 230f * frac);
                cdOverlay.enabled = cooling;
                cdText.text = cooling ? (s.CdTimer >= 1f ? Mathf.CeilToInt(s.CdTimer).ToString() : s.CdTimer.ToString("0.0")) : "";
                react.enabled = s.Bat != null;
                if (s.Bat != null) react.color = react.color.WithAlpha(0.6f + 0.4f * Mathf.Sin(Time.time * 10f));
                int charges = card?.Charges ?? 0;
                for (int k = 0; k < pips.Length; k++)
                {
                    pips[k].enabled = charges > 1 && k < charges;
                    pips[k].color = k < s.Charges ? Color.white : new Color(1f, 1f, 1f, 0.2f);
                }
                upTab.enabled = s.Up.HasValue;
                upText.enabled = s.Up.HasValue;
                upCd.enabled = s.Up.HasValue;
                if (s.Up.HasValue)
                {
                    upTab.color = Art.CardColor(CardCatalog.Get(s.Up.Value).Color) * 0.55f + new Color(0f, 0f, 0f, 0.45f);
                    var up = s.Upgrade;
                    upText.text = up.IsNone ? CardCatalog.Get(s.Up.Value).Name : up.Name;
                    upCd.rectTransform.sizeDelta = new Vector2(64f, up.Cooldown > 0f ? 170f * Mathf.Clamp01(s.UpgCd / up.Cooldown) : 0f);
                }
            }
        }
    }
}
