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
        readonly Minimap minimap;
        readonly RectTransform prompt;   // «E Взять» над ближайшей карточкой пятачка
        readonly Image promptKey;
        readonly Text promptKeyText, promptText;
        readonly RectTransform reroll;   // «F Перебросить · 3» рядом с «E Взять», пока перебросы остались
        readonly Image rerollKey;
        readonly Text rerollKeyText, rerollText;
        readonly List<Text> lootLabels = new();
        readonly List<Text> hubLabels = new(); // хаб: имена персонажей и врат
        Text speech;                           // хаб: реплика персонажа над ним // над каждой карточкой пятачка — «Форма · Свойство ◆◆»
        readonly RectTransform lootLayer;
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
            // справа сверху: волна и счёт
            waveText = UiKit.Label(t, "", 44, Pal.Text, new Vector2(1f, 1f), new Vector2(-48f, -46f), new Vector2(500f, 56f), TextAnchor.MiddleRight, FontStyle.Normal);
            waveText.rectTransform.pivot = new Vector2(1f, 0.5f);
            infoText = UiKit.Label(t, "", 21, Pal.Muted, new Vector2(1f, 1f), new Vector2(-48f, -88f), new Vector2(700f, 30f), TextAnchor.MiddleRight, FontStyle.Normal);
            infoText.rectTransform.pivot = new Vector2(1f, 0.5f);
            hint = UiKit.Label(t, "", 19, Pal.Faint, new Vector2(0f, 0f), new Vector2(48f, 36f), new Vector2(900f, 30f), TextAnchor.LowerLeft, FontStyle.Normal);
            hint.rectTransform.pivot = new Vector2(0f, 0f);
            banner = UiKit.Label(t, "", 64, new Color(1f, 0.88f, 0.4f), new Vector2(0.5f, 0.68f), Vector2.zero, new Vector2(1400f, 160f));
            minimap = new Minimap(t);
            // подсказка подбора, как «{I} Принять» над даром в Hades
            lootLayer = UiKit.Stretch(t, "LootLabels");
            speech = UiKit.Label(lootLayer, "", 24, Color.white, Vector2.zero, Vector2.zero, new Vector2(620f, 90f), TextAnchor.LowerCenter, FontStyle.Normal);
            speech.rectTransform.pivot = new Vector2(0.5f, 0f);
            speech.horizontalOverflow = HorizontalWrapMode.Wrap;
            speech.gameObject.SetActive(false);
            prompt = UiKit.Rect(t, "LootPrompt", Vector2.zero, Vector2.zero, new Vector2(600f, 40f), new Vector2(0.5f, 0.5f));
            promptKey = UiKit.Image(prompt, "Key", Pal.Gold, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(44f, 36f), new Vector2(1f, 0.5f));
            promptKeyText = UiKit.Label(promptKey.transform, "", 21, Pal.Ink, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(80f, 36f));
            promptKeyText.GetComponent<Shadow>().enabled = false;
            promptText = UiKit.Label(prompt, "", 24, Color.white, new Vector2(0.5f, 0.5f), new Vector2(10f, 0f), new Vector2(500f, 40f), TextAnchor.MiddleLeft, FontStyle.Normal);
            promptText.rectTransform.pivot = new Vector2(0f, 0.5f);
            promptText.supportRichText = true;
            prompt.gameObject.SetActive(false);
            reroll = UiKit.Rect(t, "LootReroll", Vector2.zero, Vector2.zero, new Vector2(600f, 40f), new Vector2(0.5f, 0.5f));
            rerollKey = UiKit.Image(reroll, "Key", Pal.Muted, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(44f, 36f), new Vector2(1f, 0.5f));
            rerollKeyText = UiKit.Label(rerollKey.transform, "", 21, Pal.Ink, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(80f, 36f));
            rerollKeyText.GetComponent<Shadow>().enabled = false;
            rerollText = UiKit.Label(reroll, "", 24, Pal.TextDim, new Vector2(0.5f, 0.5f), new Vector2(10f, 0f), new Vector2(500f, 40f), TextAnchor.MiddleLeft, FontStyle.Normal);
            rerollText.rectTransform.pivot = new Vector2(0f, 0.5f);
            rerollText.supportRichText = true;
            reroll.gameObject.SetActive(false);

            for (int i = 0; i < 4; i++) slots[i] = new SlotWidget(t, new Vector2(-330f + i * 220f, 30f));
            // слоты форм — справа снизу: ЛКМ, ПКМ, Q, Space — небольшие квадраты со значком формы
            const float SlotSize = 96f, SlotGap = 18f;
            for (int i = 0; i < 4; i++)
                formSlots[i] = new FormSlotWidget(t, new Vector2(-40f - (3 - i) * (SlotSize + SlotGap), 36f), new Vector2(SlotSize, SlotSize));
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
            else
            {
                txt = UiKit.Label(numbersLayer, "", 26, color, Vector2.zero, Vector2.zero, new Vector2(160f, 50f));
                txt.rectTransform.pivot = new Vector2(0.5f, 0.5f); // цифра — ровно над точкой удара
            }
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
            string room = sim.LocationTitle;
            if (room != lastRoom) { lastRoom = room; roomText.text = room; }
            if (sim.HubMode)
            {
                if (lastWave != -2) { lastWave = -2; waveText.text = Arena.Core.HubBuilder.Name; infoText.text = "Врата наверху — новый забег"; }
            }
            else if (sim.FloorMode)
            {
                var (cleared, total) = sim.FloorProgress;
                int key = sim.FloorIndex * 100000 + cleared * 1000 + sim.Kills;
                if (key != lastWave)
                {
                    lastWave = key;
                    waveText.text = $"Этаж {sim.FloorIndex + 1}";
                    infoText.text = $"Комнаты {cleared}/{total}   ·   Убито {sim.Kills}";
                }
            }
            else if (sim.Wave != lastWave || sim.Kills != lastKills || sim.Rewards != lastRewards)
            {
                lastWave = sim.Wave; lastKills = sim.Kills; lastRewards = sim.Rewards;
                waveText.text = $"Волна {Mathf.Max(1, sim.Wave)}";
                infoText.text = $"Убито {sim.Kills}";
            }
            minimap.Sync(sim);
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

            // над каждой карточкой — «Форма · Свойство» одной строкой, у ближайшей под ним «E Взять»
            var nearCard = cam != null && !paused ? sim.LootNear : null;
            bool labels = sim.Loot.Count > 0 && sim.Time >= sim.LootReadyAt && !sim.RewardPending && cam != null && !paused;
            while (lootLabels.Count < sim.Loot.Count)
            {
                var l = UiKit.Label(lootLayer, "", 24, Color.white, Vector2.zero, Vector2.zero, new Vector2(420f, 34f));
                l.rectTransform.pivot = new Vector2(0.5f, 0f);
                l.supportRichText = true;
                lootLabels.Add(l);
            }
            const float Gap = 12f, PromptH = 36f;
            for (int i = 0; i < lootLabels.Count; i++)
            {
                var l = lootLabels[i];
                bool show = labels && i < sim.Loot.Count;
                if (l.gameObject.activeSelf != show) l.gameObject.SetActive(show);
                if (!show) continue;
                var g = sim.Loot[i];
                if (g.Trinket != null) l.text = TrinketLabel(g.Trinket);
                else
                {
                    var c = g.Card;
                    // после названия — ромбики по числу вливаний, цветом редкости
                    string pips = $" <color=#{ColorUtility.ToHtmlStringRGB(Art.RarityColor(c.Rarity))}>{new string('◆', c.SoulCells)}</color>";
                    l.text = (c.Own.HasValue
                        ? $"{FormText.Name(c.Form)} · <color=#{ColorUtility.ToHtmlStringRGB(Art.CardTint(c))}>{FormText.Name(c.Own.Value)}</color>"
                        : FormText.Name(c.Form)) + pips;
                }
                var vp = cam.WorldToViewportPoint(Art.W(g.Pos, LootView.CardTop));
                float y = vp.y * size.y + Gap + (g == nearCard ? PromptH + 4f : 0f);
                l.rectTransform.anchoredPosition = new Vector2(vp.x * size.x, y);
            }

            // хаб: имена над вратами и персонажами, реплика над тем, кто говорит
            var hubNear = cam != null && !paused ? sim.HubNear : null;
            while (hubLabels.Count < sim.HubSpots.Count)
            {
                var l = UiKit.Label(lootLayer, "", 24, Color.white, Vector2.zero, Vector2.zero, new Vector2(320f, 34f));
                l.rectTransform.pivot = new Vector2(0.5f, 0f);
                hubLabels.Add(l);
            }
            for (int i = 0; i < hubLabels.Count; i++)
            {
                var l = hubLabels[i];
                bool show = i < sim.HubSpots.Count && cam != null && !paused;
                if (l.gameObject.activeSelf != show) l.gameObject.SetActive(show);
                if (!show) continue;
                var spot = sim.HubSpots[i];
                l.text = spot.Name;
                l.color = Color.Lerp(spot.Color, Color.white, 0.4f);
                var vp = cam.WorldToViewportPoint(Art.W(spot.Pos, HubLabelY(spot)));
                l.rectTransform.anchoredPosition = new Vector2(vp.x * size.x, vp.y * size.y + Gap + (spot == hubNear ? PromptH + 4f : 0f));
            }
            bool talking = sim.HubMode && sim.SpeechSpot != null && sim.Time - sim.SpeechAt < 4.5f && cam != null && !paused;
            if (speech.gameObject.activeSelf != talking) speech.gameObject.SetActive(talking);
            if (talking)
            {
                speech.text = sim.SpeechText;
                var vp = cam.WorldToViewportPoint(Art.W(sim.SpeechSpot.Pos, HubLabelY(sim.SpeechSpot)));
                speech.rectTransform.anchoredPosition = new Vector2(vp.x * size.x, vp.y * size.y + Gap + PromptH + 44f);
            }

            bool near = nearCard != null || hubNear != null;
            if (prompt.gameObject.activeSelf != near) prompt.gameObject.SetActive(near);
            if (near)
            {
                var vp = cam.WorldToViewportPoint(nearCard != null ? Art.W(nearCard.Pos, LootView.CardTop) : Art.W(hubNear.Pos, HubLabelY(hubNear)));
                string key = useMouse ? "E" : "RB";
                if (promptKeyText.text != key)
                {
                    promptKeyText.text = key;
                    promptKey.rectTransform.sizeDelta = new Vector2(promptKeyText.preferredWidth + 20f, PromptH);
                }
                promptText.text = nearCard != null ? "Взять" : hubNear.Kind == Arena.Core.HubSpotKind.Gate ? "Начать забег" : hubNear.Kit ? "Стартовый набор" : "Говорить";
                // ключ и слово — по центру под названием
                float keyW = promptKey.rectTransform.sizeDelta.x;
                float w = keyW + 10f + promptText.preferredWidth;
                // переброс — той же строкой справа: «E Взять   F Перебросить · 3»
                bool canReroll = nearCard != null && sim.CanReroll;
                if (reroll.gameObject.activeSelf != canReroll) reroll.gameObject.SetActive(canReroll);
                float w2 = 0f, key2W = 0f;
                if (canReroll)
                {
                    string rk = useMouse ? "F" : "LB";
                    if (rerollKeyText.text != rk)
                    {
                        rerollKeyText.text = rk;
                        rerollKey.rectTransform.sizeDelta = new Vector2(rerollKeyText.preferredWidth + 20f, PromptH);
                    }
                    rerollText.text = $"Перебросить <color=#{ColorUtility.ToHtmlStringRGB(Pal.Faint)}>· {sim.RerollsLeft}</color>";
                    key2W = rerollKey.rectTransform.sizeDelta.x;
                    w2 = 28f + key2W + 10f + rerollText.preferredWidth;
                }
                float left = vp.x * size.x - (w + w2) * 0.5f, y = vp.y * size.y + Gap + PromptH * 0.5f;
                prompt.anchoredPosition = new Vector2(left + keyW, y);
                if (canReroll) reroll.anchoredPosition = new Vector2(left + w + 28f + key2W, y);
            }
            else if (reroll.gameObject.activeSelf) reroll.gameObject.SetActive(false);
        }

        /// <summary>Над чем в хабе пишется имя: над вратами — выше перемычки, над персонажем — над головой.</summary>
        static float HubLabelY(Arena.Core.HubSpot s) => s.Kind == Arena.Core.HubSpotKind.Gate ? 3.8f : 2.1f;

        /// <summary>«Уголёк · Пламя I»: имя, категория цветом тринкета, уровень.</summary>
        static string TrinketLabel(Trinket t) =>
            $"{TrinketBook.Name(t.Kind)} · <color=#{ColorUtility.ToHtmlStringRGB(Art.TrinketColor(t.Kind))}>{TrinketBook.Category(t.Kind)} {TrinketBook.Roman(t.Level)}</color>";

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

        /// <summary>Слот режима форм в бою — небольшой квадрат: клавиша на плашке, значок формы одним цветом, перезарядка —
        /// затемнение сверху и число, заряд — шкала над слотом. Свойства и души здесь не показываются — они в окне
        /// замены и вливания (RewardScreen) и в паузе.</summary>
        sealed class FormSlotWidget
        {
            static readonly Color Icon = new(0.91f, 0.87f, 0.78f);
            readonly float W, H;
            readonly RectTransform root;
            readonly Image frame, bg, cdOverlay, highlight, chargeBg, chargeFill;
            readonly Text glyph, cdText, keyText;
            FormKind? shownForm;
            string shownKey;
            int shownCd = -1;

            public FormSlotWidget(Transform parent, Vector2 pos, Vector2 size)
            {
                W = size.x; H = size.y;
                root = UiKit.Rect(parent, "FormSlot", new Vector2(1f, 0f), pos, size, new Vector2(1f, 0f));
                highlight = UiKit.Image(root, "Highlight", new Color(1f, 1f, 1f, 0.8f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(W + 8f, H + 8f));
                frame = UiKit.Image(root, "Frame", Pal.GoldDim, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(W, H));
                bg = UiKit.Image(root, "Bg", Pal.Card, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(W - 4f, H - 4f));
                glyph = UiKit.Label(root, "", Mathf.RoundToInt(H * 0.5f), Icon, new Vector2(0.5f, 0.5f), new Vector2(0f, -2f), new Vector2(W, H));
                glyph.rectTransform.pivot = new Vector2(0.5f, 0.5f);
                cdOverlay = UiKit.Image(root, "Cd", new Color(0.02f, 0f, 0.05f, 0.72f), new Vector2(0.5f, 1f), new Vector2(0f, -2f), new Vector2(W - 4f, 0f), new Vector2(0.5f, 1f));
                cdText = UiKit.Label(root, "", 30, Color.white, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(W, H));
                cdText.rectTransform.pivot = new Vector2(0.5f, 0.5f);
                // шкала заряда над слотом: золотая — копится, белая — окно «идеального» выпуска
                chargeBg = UiKit.Image(root, "ChargeBg", new Color(0f, 0f, 0f, 0.6f), new Vector2(0.5f, 1f), new Vector2(0f, 20f), new Vector2(W, 8f), new Vector2(0.5f, 0f));
                chargeFill = UiKit.Image(chargeBg.transform, "Charge", Pal.Gold, new Vector2(0f, 0.5f), Vector2.zero, new Vector2(0f, 8f), new Vector2(0f, 0.5f));
                var badge = UiKit.Image(root, "Key", Pal.Gold, new Vector2(0f, 1f), new Vector2(-8f, 10f), new Vector2(56f, 24f), new Vector2(0f, 1f));
                keyText = UiKit.Label(badge.transform, "", 14, Pal.Ink, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(56f, 24f));
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
                glyph.enabled = has;
                frame.color = has ? Pal.GoldDim : new Color(1f, 1f, 1f, 0.12f);
                bg.color = has ? Pal.Card : Pal.Card.WithAlpha(0.6f);
                if (has && card.Form != shownForm) { shownForm = card.Form; glyph.text = FormText.Glyph(card.Form); }
                highlight.enabled = has && s.Holding;
                chargeBg.enabled = chargeFill.enabled = has && s.Holding;
                if (has && s.Holding)
                {
                    // до порога — золотая растёт, в окне — белая, окно пропущено — тусклая
                    float k = sim.ChargeOf(s);
                    bool window = sim.InChargeWindow(s), missed = sim.ChargeMissed(s);
                    chargeFill.rectTransform.sizeDelta = new Vector2(W * k, 8f);
                    chargeFill.color = window ? Color.white : missed ? new Color(0.45f, 0.42f, 0.5f) : Pal.Gold;
                    highlight.color = window ? Color.white : missed ? new Color(1f, 1f, 1f, 0.15f) : new Color(1f, 1f, 1f, 0.35f + 0.45f * k);
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
