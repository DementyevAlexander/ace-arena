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
        static readonly string[] KeysMouse = { "ЛКМ", "ПКМ", "Q", "Space" }; // раскладка Hades II
        static readonly string[] KeysPad = { "X", "Y", "B", "A" };

        const float HpW = 520f, HpH = 34f;
        readonly Canvas canvas;
        readonly Image hpFill;
        readonly Text hpText, roomText, waveText, infoText, banner, buffText, hint;
        readonly Image[] ticks = new Image[8];
        readonly SlotWidget[] slots = new SlotWidget[4];
        readonly FormSlotWidget[] formSlots = new FormSlotWidget[4];
        readonly RectTransform numbersLayer;
        readonly List<FloatNumber> numbers = new();
        readonly Stack<Text> freeNumbers = new();
        const int MaxNumbers = 48; // больше — старые гаснут раньше
        int lastHp = -1, lastWave = -1, lastKills = -1, lastRewards = -1;
        string lastRoom;
        readonly Image hurtFlash;
        float hurtFade = 0.3f;
        float bannerT;

        public Hud()
        {
            canvas = UiKit.Canvas("HUD", 10);
            var t = canvas.transform;
            hurtFlash = UiKit.Fill(t, "HurtFlash", new Color(1f, 0.1f, 0.1f, 0f));
            numbersLayer = UiKit.Stretch(t, "Numbers");

            // слева сверху: имя, комната, здоровье
            var name = UiKit.Label(t, "Смерть", 36, Pal.Gold, new Vector2(0f, 1f), new Vector2(48f, -42f), new Vector2(300f, 46f), TextAnchor.MiddleLeft, FontStyle.Normal);
            name.rectTransform.pivot = new Vector2(0f, 0.5f);
            roomText = UiKit.Label(t, "", 21, Pal.Muted, new Vector2(0f, 1f), new Vector2(48f + name.preferredWidth + 18f, -45f), new Vector2(600f, 30f), TextAnchor.MiddleLeft, FontStyle.Normal);
            roomText.rectTransform.pivot = new Vector2(0f, 0.5f);
            var hpFrame = UiKit.Image(t, "HpFrame", Pal.GoldDim, new Vector2(0f, 1f), new Vector2(48f, -70f), new Vector2(HpW + 4f, HpH + 4f), new Vector2(0f, 1f));
            var hpBg = UiKit.Image(hpFrame.transform, "HpBg", new Color(0.16f, 0.09f, 0.19f, 0.95f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(HpW, HpH));
            hpFill = UiKit.Image(hpBg.transform, "HpFill", new Color(0.85f, 0.29f, 0.39f), new Vector2(0f, 0.5f), Vector2.zero, new Vector2(HpW, HpH), new Vector2(0f, 0.5f));
            hpText = UiKit.Label(hpBg.transform, "", 21, Color.white, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(HpW, HpH));
            buffText = UiKit.Label(t, "", 20, new Color(0.6f, 1f, 1f), new Vector2(0f, 1f), new Vector2(48f, -132f), new Vector2(700f, 28f), TextAnchor.MiddleLeft, FontStyle.Normal);
            buffText.rectTransform.pivot = new Vector2(0f, 0.5f);
            // справа сверху: волна, деления до карточки, счёт
            waveText = UiKit.Label(t, "", 44, Pal.Text, new Vector2(1f, 1f), new Vector2(-48f, -46f), new Vector2(500f, 56f), TextAnchor.MiddleRight, FontStyle.Normal);
            waveText.rectTransform.pivot = new Vector2(1f, 0.5f);
            for (int i = 0; i < ticks.Length; i++)
                ticks[i] = UiKit.Image(t, "Tick", Pal.Gold, new Vector2(1f, 1f), new Vector2(-48f - i * 54f, -86f), new Vector2(46f, 8f), new Vector2(1f, 0.5f));
            infoText = UiKit.Label(t, "", 21, Pal.Muted, new Vector2(1f, 1f), new Vector2(-48f, -114f), new Vector2(700f, 30f), TextAnchor.MiddleRight, FontStyle.Normal);
            infoText.rectTransform.pivot = new Vector2(1f, 0.5f);
            hint = UiKit.Label(t, "", 19, Pal.Faint, new Vector2(0f, 0f), new Vector2(48f, 36f), new Vector2(900f, 30f), TextAnchor.LowerLeft, FontStyle.Normal);
            hint.rectTransform.pivot = new Vector2(0f, 0f);
            banner = UiKit.Label(t, "", 64, new Color(1f, 0.88f, 0.4f), new Vector2(0.5f, 0.68f), Vector2.zero, new Vector2(1400f, 160f));

            for (int i = 0; i < 4; i++) slots[i] = new SlotWidget(t, new Vector2(-330f + i * 220f, 30f));
            // слоты форм — справа снизу, как в Hades II: ЛКМ, ПКМ, Q и меньший слот рывка (Space)
            float right = -40f;
            for (int i = 3; i >= 0; i--)
            {
                bool dash = i == ArenaSim.DashSlot;
                var size = dash ? new Vector2(150f, 184f) : new Vector2(170f, 224f);
                formSlots[i] = new FormSlotWidget(t, new Vector2(right, 36f), size);
                right -= size.x + 24f;
            }
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
            if (numbers.Count >= MaxNumbers) { Release(numbers[0].Text); numbers.RemoveAt(0); }
            Text txt;
            if (freeNumbers.Count > 0) { txt = freeNumbers.Pop(); txt.gameObject.SetActive(true); }
            else txt = UiKit.Label(numbersLayer, "", 26, color, Vector2.zero, Vector2.zero, new Vector2(160f, 50f));
            txt.text = Mathf.RoundToInt(value).ToString();
            txt.fontSize = crit ? 36 : 26;
            txt.color = color;
            numbers.Add(new FloatNumber { Text = txt, World = new Vector3(world.x + Random.Range(-0.25f, 0.25f), 1.5f, world.y), T = 0f });
        }

        public void Sync(ArenaSim sim, Camera cam, bool useMouse, float dt, bool paused)
        {
            var p = sim.Player;
            float k = Mathf.Clamp01(p.Hp / sim.PlayerCfg.hp);
            hpFill.rectTransform.sizeDelta = new Vector2(HpW * k, HpH);
            int hp = Mathf.CeilToInt(p.Hp);
            if (hp != lastHp) { lastHp = hp; hpText.text = $"{hp} / {sim.PlayerCfg.hp:0}"; }
            string room = sim.Layout != null && !string.IsNullOrEmpty(sim.Layout.title) ? sim.Layout.title : sim.Room.name;
            if (room != lastRoom) { lastRoom = room; roomText.text = room; }
            if (sim.Wave != lastWave || sim.Kills != lastKills || sim.Rewards != lastRewards)
            {
                lastWave = sim.Wave; lastKills = sim.Kills; lastRewards = sim.Rewards;
                waveText.text = $"Волна {Mathf.Max(1, sim.Wave)}";
                // карточка — после каждой N-й волны, когда она добита: деления — пройденные волны этого круга
                int per = Mathf.Clamp(sim.ArenaCfg.wavesPerReward, 1, ticks.Length);
                int wave = Mathf.Max(1, sim.Wave), done = (wave - 1) % per, next = (wave - 1) / per * per + per;
                for (int i = 0; i < ticks.Length; i++)
                {
                    ticks[i].enabled = sim.FormsMode && i < per;
                    ticks[i].color = per - 1 - i < done ? Pal.Gold : Pal.KeyDark;
                }
                infoText.text = sim.FormsMode ? $"Карточка после волны {next}   ·   Убито {sim.Kills}" : $"Убито {sim.Kills}";
            }
            if (!sim.FormsMode)
            {
                var buffs = new List<string>();
                if (p.Haste > 0f) buffs.Add($"Haste {p.Haste:0.0}с");
                if (p.BuffInvuln > 0f) buffs.Add($"Invulnerability {p.BuffInvuln:0.0}с");
                if (p.Shoes > 0f) buffs.Add($"Blazing Shoes {p.Shoes:0.0}с");
                if (p.Shield > 0f) buffs.Add("Aegis");
                buffText.text = string.Join("   ", buffs);
            }
            else if (buffText.text.Length > 0) buffText.text = "";
            var keys = useMouse ? KeysMouse : KeysPad;
            if (sim.FormsMode)
            {
                hint.text = useMouse ? "Tab — скиллы   ·   Esc — пауза" : "View — скиллы   ·   Menu — пауза";
                for (int i = 0; i < 4; i++) { slots[i].SetVisible(false); formSlots[i].Sync(sim, i, keys[i]); }
            }
            else
            {
                hint.text = useMouse
                    ? "WASD — бег · ЛКМ атака · ПКМ спецатака · Q каст · Space/Shift рывок (держать — спринт)\nTab — колода · Esc — пауза · R — заново"
                    : "Стик — бег и прицел · X атака · Y спецатака · B каст · A рывок (держать — спринт)\nView — колода · Menu — пауза";
                for (int i = 0; i < 4; i++) { formSlots[i].SetVisible(false); slots[i].SetVisible(true); slots[i].Sync(sim, i, keys[i], sim.Player.Cast != null && sim.Player.Cast.Slot == i); }
            }

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
                if (n.T > 0.8f || cam == null) { Release(n.Text); numbers.RemoveAt(i); continue; }
                var vp = cam.WorldToViewportPoint(n.World);
                n.Text.rectTransform.anchoredPosition = new Vector2(vp.x * size.x, vp.y * size.y);
                n.Text.color = n.Text.color.WithAlpha(Mathf.Clamp01((0.8f - n.T) * 3f));
            }
        }

        public void SetVisible(bool v) => canvas.enabled = v;

        void Release(Text t)
        {
            t.gameObject.SetActive(false);
            freeNumbers.Push(t);
        }

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

            public void SetVisible(bool v) { if (root.gameObject.activeSelf != v) root.gameObject.SetActive(v); }

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

        /// <summary>Слот режима форм — вертикальная карта, как в Hades II: клавиша на плашке, ячейки душ справа сверху,
        /// значок формы, имя, свойства с уровнями; перезарядка — затемнение сверху и число.</summary>
        sealed class FormSlotWidget
        {
            readonly float W, H;
            readonly RectTransform root;
            readonly Image frame, bg, cdOverlay, highlight;
            readonly Text glyph, title, traits, cdText, keyText, empty;
            readonly Image[] cells = new Image[SkillCard.SoulCells];
            SkillCard shownCard;
            int shownSouls = -1;
            string shownKey;
            int shownCd = -1;

            public FormSlotWidget(Transform parent, Vector2 pos, Vector2 size)
            {
                W = size.x; H = size.y;
                root = UiKit.Rect(parent, "FormSlot", new Vector2(1f, 0f), pos, size, new Vector2(1f, 0f));
                highlight = UiKit.Image(root, "Highlight", new Color(1f, 1f, 1f, 0.8f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(W + 10f, H + 10f));
                frame = UiKit.Image(root, "Frame", Color.white, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(W, H));
                bg = UiKit.Image(root, "Bg", Pal.Card, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(W - 4f, H - 4f));
                glyph = UiKit.Label(root, "", Mathf.RoundToInt(H * 0.28f), Color.white, new Vector2(0.5f, 1f), new Vector2(0f, -H * 0.3f), new Vector2(W, H * 0.34f));
                glyph.rectTransform.pivot = new Vector2(0.5f, 0.5f);
                title = UiKit.Label(root, "", 22, Color.white, new Vector2(0.5f, 1f), new Vector2(0f, -H * 0.6f), new Vector2(W - 12f, 30f), TextAnchor.MiddleCenter, FontStyle.Normal);
                title.rectTransform.pivot = new Vector2(0.5f, 0.5f);
                traits = UiKit.Label(root, "", 17, Pal.TextDim, new Vector2(0.5f, 1f), new Vector2(0f, -H * 0.6f - 18f), new Vector2(W - 12f, H * 0.4f), TextAnchor.UpperCenter, FontStyle.Normal);
                traits.rectTransform.pivot = new Vector2(0.5f, 1f);
                traits.supportRichText = true;
                for (int k = 0; k < cells.Length; k++)
                    cells[k] = UiKit.Image(root, "Cell", Color.white, new Vector2(1f, 1f), new Vector2(-16f - k * 20f, -16f), new Vector2(13f, 13f));
                empty = UiKit.Label(root, "пусто", 21, Pal.Faint, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(W, 40f), TextAnchor.MiddleCenter, FontStyle.Normal);
                cdOverlay = UiKit.Image(root, "Cd", new Color(0.02f, 0f, 0.05f, 0.72f), new Vector2(0.5f, 1f), new Vector2(0f, -2f), new Vector2(W - 4f, 0f), new Vector2(0.5f, 1f));
                cdText = UiKit.Label(root, "", 44, Color.white, new Vector2(0.5f, 1f), new Vector2(0f, -H * 0.3f), new Vector2(W, 60f));
                cdText.rectTransform.pivot = new Vector2(0.5f, 0.5f);
                var badge = UiKit.Image(root, "Key", Pal.Gold, new Vector2(0f, 1f), new Vector2(-12f, 14f), new Vector2(84f, 32f), new Vector2(0f, 1f));
                keyText = UiKit.Label(badge.transform, "", 18, Pal.Ink, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(84f, 32f));
                keyText.GetComponent<Shadow>().enabled = false;
            }

            public void SetVisible(bool v) { if (root.gameObject.activeSelf != v) root.gameObject.SetActive(v); }

            public void Sync(ArenaSim sim, int i, string key)
            {
                SetVisible(true);
                var s = sim.Forms[i];
                var card = s.Card;
                if (key != shownKey) { shownKey = key; keyText.text = key; }
                bool has = card != null;
                glyph.enabled = title.enabled = traits.enabled = has;
                empty.enabled = !has;
                foreach (var c in cells) c.enabled = has;
                var col = Art.CardTint(card);
                frame.color = has ? col * 0.8f + new Color(0f, 0f, 0f, 0.2f) : new Color(1f, 1f, 1f, 0.12f);
                bg.color = has ? Pal.Card : Pal.Card.WithAlpha(0.8f);
                highlight.enabled = has && card.Form == FormKind.Shot && s.Charging;
                if (has && (card != shownCard || card.Souls.Count != shownSouls))
                {
                    shownCard = card; shownSouls = card.Souls.Count;
                    glyph.text = FormText.Glyph(card.Form);
                    glyph.color = col;
                    title.text = FormText.Name(card.Form);
                    var sb = new System.Text.StringBuilder();
                    foreach (var t in card.Traits)
                    {
                        int lvl = card.Level(t);
                        if (sb.Length > 0) sb.Append('\n');
                        sb.Append($"<color=#{ColorUtility.ToHtmlStringRGB(Art.TraitColor(t))}>{FormText.Name(t)}{(lvl > 1 ? $" ×{lvl}" : "")}</color>");
                    }
                    traits.text = sb.Length > 0 ? sb.ToString() : "<color=#8a809a>без свойств</color>";
                    for (int k = 0; k < cells.Length; k++) cells[k].color = k < card.Souls.Count ? Art.TraitColor(card.Souls[k]) : new Color(1f, 1f, 1f, 0.18f);
                }
                bool cooling = has && s.Cd > 0f && s.CdTotal > 0f;
                float frac = cooling ? Mathf.Clamp01(s.Cd / s.CdTotal) : 0f;
                cdOverlay.enabled = cooling;
                cdOverlay.rectTransform.sizeDelta = new Vector2(W - 4f, (H - 4f) * frac);
                // подпись перезарядки — только когда меняется показанное число (десятые доли под секундой)
                int cdKey = cooling && s.Cd < 50f ? (s.Cd >= 1f ? 1000 + Mathf.CeilToInt(s.Cd) : Mathf.CeilToInt(s.Cd * 10f)) : -1;
                if (cdKey != shownCd)
                {
                    shownCd = cdKey;
                    cdText.text = cdKey < 0 ? "" : cdKey >= 1000 ? (cdKey - 1000).ToString() : (cdKey / 10f).ToString("0.0");
                }
            }
        }
    }
}
