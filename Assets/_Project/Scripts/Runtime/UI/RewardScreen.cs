using Arena.Core;
using Arena.Sim;
using Arena.View;
using UnityEngine;
using UnityEngine.UI;

namespace Arena.UI
{
    /// <summary>Подобранная карточка и просмотр скиллов (Tab). Сверху две карты рядом: «Сейчас в слоте» и «Получено» —
    /// видно, что на что меняешь; у каждой — редкость и ячейки душ по ней. Ниже ряд слотов и две кнопки: «Заменить» / «Вставить» (A) и «Влить» (Y);
    /// под каждой — что пропадёт или что станет. Недоступная кнопка погашена и объясняет почему.
    /// Слот — ← → (стик, крестовина, стрелки) или клик; B / Esc — оставить карточку на земле.
    /// В просмотре скиллов — только карта слота в фокусе и ряд слотов.</summary>
    public sealed class RewardScreen
    {
        static readonly string[] KeysMouse = { "ЛКМ", "ПКМ", "Q", "Space" };
        static readonly string[] KeysPad = { "X", "Y", "B", "A" };
        static readonly Color Got = new(1f, 0.6f, 0.2f);
        static readonly Color TakeTint = new(0.72f, 0.58f, 1f);
        static readonly Color FuseTint = new(0.3f, 0.86f, 0.74f);
        static readonly Color Loss = new(1f, 0.47f, 0.42f);
        static readonly Color Gain = new(1f, 0.85f, 0.35f);
        const float InputDelay = 0.35f; // рывок или «взять», зажатые в бою, не должны сразу нажать кнопку

        readonly Canvas canvas;
        readonly ArenaSim sim;
        readonly CardPanel current, offered;
        readonly GameObject swap, actionsRow, leaveHint;
        readonly Text actionsCaption;
        readonly SlotButton[] slots = new SlotButton[4];
        readonly ActionButton take, fuse;
        readonly Text viewTitle;
        int focus;
        bool viewOnly, pad;
        float openedAt;

        public bool Visible => canvas.enabled;

        public RewardScreen(ArenaSim sim)
        {
            this.sim = sim;
            canvas = UiKit.Canvas("Reward", 25);
            UiKit.Fill(canvas.transform, "Dim", new Color(0.03f, 0.02f, 0.05f, 0.72f)).raycastTarget = true;
            // макет 1920×1080: координаты от левого верхнего угла
            var t = UiKit.Rect(canvas.transform, "Layout", new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1920f, 1080f));

            viewTitle = Lbl(t, "Скиллы", 44, Pal.Gold, 60f, 70f, 800f, 60f);
            current = new CardPanel(t, 60f, 165f);
            offered = new CardPanel(t, 1010f, 165f);
            var sw = UiKit.Image(t, "Swap", Pal.GoldDim, new Vector2(0f, 1f), new Vector2(960f, -365f), new Vector2(76f, 76f), new Vector2(0.5f, 0.5f));
            UiKit.Image(sw.transform, "Bg", Pal.Card, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(72f, 72f));
            UiKit.Label(sw.transform, "⇄", 40, Pal.Text, new Vector2(0.5f, 0.5f), new Vector2(0f, 2f), new Vector2(76f, 76f), TextAnchor.MiddleCenter, FontStyle.Normal);
            swap = sw.gameObject;

            var box = UiKit.Image(t, "Box", Pal.Line, new Vector2(0f, 1f), new Vector2(60f, -590f), new Vector2(1800f, 278f), new Vector2(0f, 1f));
            UiKit.Image(box.transform, "Bg", new Color(0.07f, 0.055f, 0.1f, 0.92f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1796f, 274f));
            Lbl(box.transform, "СЛОТ", 22, Pal.Muted, 30f, 66f, 200f, 30f);
            for (int i = 0; i < 4; i++) slots[i] = new SlotButton(this, box.transform, i, 224f + i * 391f, 22f);
            actionsRow = UiKit.Rect(box.transform, "Actions", new Vector2(0f, 1f), Vector2.zero, new Vector2(1800f, 278f), new Vector2(0f, 1f)).gameObject;
            actionsCaption = Lbl(actionsRow.transform, "ДЕЙСТВИЕ", 22, Pal.Muted, 30f, 192f, 200f, 30f);
            take = new ActionButton(actionsRow.transform, 224f, 134f, 762f, TakeTint, new Color(0.17f, 0.12f, 0.26f), () => Take());
            fuse = new ActionButton(actionsRow.transform, 1006f, 134f, 762f, FuseTint, new Color(0.07f, 0.19f, 0.17f), () => Fuse());

            var leave = UiKit.Label(t, "", 22, Pal.Muted, new Vector2(0f, 1f), new Vector2(960f, -905f), new Vector2(800f, 36f), TextAnchor.MiddleCenter, FontStyle.Normal);
            leave.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            leave.raycastTarget = true;
            var lb = leave.gameObject.AddComponent<Button>();
            lb.transition = Selectable.Transition.None;
            lb.onClick.AddListener(() => { if (!viewOnly) sim.LeaveLoot(); });
            leaveHint = leave.gameObject;
            canvas.enabled = false;
        }

        static Text Lbl(Transform parent, string text, int size, Color color, float x, float y, float w, float h, FontStyle style = FontStyle.Normal)
        {
            var l = UiKit.Label(parent, text, size, color, new Vector2(0f, 1f), new Vector2(x, -y), new Vector2(w, h), TextAnchor.MiddleLeft, style);
            l.rectTransform.pivot = new Vector2(0f, 0.5f);
            l.supportRichText = true;
            return l;
        }

        static string Hex(Color c) => ColorUtility.ToHtmlStringRGB(c);

        /// <summary>Показывает выбор, пока он не сделан; Tab — просмотр скиллов без выбора.</summary>
        public void Sync(bool viewRequested, InputReader input)
        {
            bool want = (sim.RewardPending && sim.TrinketOffer == null) || viewRequested; // тринкет — свой экран (TrinketScreen)
            if (want != canvas.enabled)
            {
                canvas.enabled = want;
                openedAt = Time.unscaledTime;
                if (want) focus = sim.RewardPending && sim.Offers.Count > 0 ? BestSlot() : 0;
            }
            if (!want) return;
            viewOnly = !sim.RewardPending || sim.Offers.Count == 0;
            pad = !input.UseMouse;
            if (Time.unscaledTime - openedAt >= InputDelay)
            {
                if (input.NavX != 0) focus = (focus + input.NavX + 4) % 4;
                if (!viewOnly)
                {
                    if (input.NavConfirm) Take();
                    else if (input.NavAlt) Fuse();
                    else if (input.NavBack || input.PausePressed) sim.LeaveLoot();
                }
            }
            // выбор сделан или карточка оставлена — экран закроется; иначе перерисовать по текущему состоянию
            viewOnly = !sim.RewardPending || sim.Offers.Count == 0;
            if (!sim.RewardPending && !viewRequested) { canvas.enabled = false; return; }
            Draw(input.UseMouse);
        }

        /// <summary>С какого слота начать: рывок — Space; иначе пустой подходящий слот, иначе первый, куда можно влить душу.</summary>
        int BestSlot()
        {
            for (int i = 0; i < 4; i++) if (sim.CanTake(0, i) && sim.Forms[i].Card == null) return i;
            for (int i = 0; i < 4; i++) if (sim.CanFuse(0, i)) return i;
            for (int i = 0; i < 4; i++) if (sim.CanTake(0, i)) return i;
            return 0;
        }

        void Take() { if (!viewOnly && sim.CanTake(0, focus)) sim.TakeOffer(0, focus); }

        void Fuse() { if (!viewOnly && sim.CanFuse(0, focus)) sim.FuseOffer(0, focus); }

        void Draw(bool mouse)
        {
            var keys = mouse ? KeysMouse : KeysPad;
            var cur = sim.Forms[focus].Card;
            string slotKey = KeysMouse[focus];
            viewTitle.gameObject.SetActive(viewOnly);
            offered.SetActive(!viewOnly);
            swap.SetActive(!viewOnly);
            actionsRow.SetActive(!viewOnly);
            leaveHint.SetActive(!viewOnly);

            if (cur != null) current.Show(cur, (viewOnly ? $"СЛОТ {slotKey}" : $"СЕЙЧАС В {slotKey}").ToUpperInvariant(), Pal.Gold, Pal.Gold);
            else current.ShowEmpty($"СЕЙЧАС В {slotKey}".ToUpperInvariant(), focus == ArenaSim.DashSlot ? "Сюда ставится только рывок." : "Сюда можно поставить снаряд, зону, призыв или барьер.");
            for (int i = 0; i < 4; i++) slots[i].Sync(sim.Forms[i].Card, keys[i], i == focus);
            if (viewOnly) return;

            var got = sim.Offers[0];
            offered.Show(got, "ПОЛУЧЕНО", Got, Got);
            leaveHint.GetComponent<Text>().text = mouse ? "Esc — оставить на земле" : "B — оставить на земле";
            actionsCaption.text = "ДЕЙСТВИЕ";

            // A: заменить (занятый слот) или вставить (пустой)
            string gotName = FormText.Name(got.Form);
            if (!sim.CanTake(0, focus))
                take.Set(pad ? "A" : "", cur != null ? $"Заменить на {gotName}" : $"Вставить {gotName}",
                    got.Form == FormKind.Dash ? "Рывок ставится только в Space" : "Space — только для рывка", Pal.Faint, false);
            else if (cur == null)
                take.Set(pad ? "A" : "", $"Вставить {gotName} в {slotKey}", "Ничего не пропадёт", Pal.Muted, true);
            else
            {
                var sb = new System.Text.StringBuilder($"− {FormText.Name(cur.Form)} пропадёт");
                var traits = cur.Traits;
                for (int k = 0; k < traits.Count; k++) sb.Append(k == 0 ? " вместе с " : k == traits.Count - 1 ? " и " : ", ").Append(FormText.Name(traits[k]));
                take.Set(pad ? "A" : "", $"Заменить на {gotName}", sb.ToString(), Loss, true);
            }

            // Y: влить свойство карточки в карту слота
            var trait = got.Own.Value;
            if (cur == null) fuse.Set(pad ? "Y" : "", $"Влить {FormText.Name(trait)}", "Слот пуст — вливать некуда", Pal.Faint, false);
            else if (!cur.CanFuse) fuse.Set(pad ? "Y" : "", $"Влить {FormText.Name(trait)} в {FormText.Name(cur.Form)}", "Ячейки душ заполнены", Pal.Faint, false);
            else
            {
                int lvl = cur.Level(trait) + 1;
                string what = lvl > 1 ? $"▲ {FormText.Name(trait)} ×{lvl}" : $"+ {FormText.Name(trait)}: {FormText.Cell(trait, cur.Form)}";
                fuse.Set(pad ? "Y" : "", $"Влить {FormText.Name(trait)} в {FormText.Name(cur.Form)}", $"{what} · {gotName} исчезнет", Gain, true);
            }
        }

        // ---------- карта сверху ----------

        sealed class CardPanel
        {
            const float W = 850f, H = 400f;
            readonly GameObject root;
            readonly Image frame;
            readonly Text caption, glyph, name, desc, traits, souls;
            readonly Image[] cells = new Image[SkillCard.MaxSoulCells];

            public CardPanel(Transform parent, float x, float y)
            {
                var r = UiKit.Rect(parent, "Card", new Vector2(0f, 1f), new Vector2(x, -y), new Vector2(W, H), new Vector2(0f, 1f));
                root = r.gameObject;
                frame = UiKit.Image(r, "Frame", Pal.Gold, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(W, H));
                UiKit.Image(r, "Bg", new Color(0.09f, 0.07f, 0.13f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(W - 4f, H - 4f));
                caption = Lbl(r, "", 22, Pal.Gold, 34f, 42f, 700f, 30f, FontStyle.Bold);
                glyph = UiKit.Label(r, "", 50, Color.white, new Vector2(0f, 1f), new Vector2(66f, -98f), new Vector2(70f, 70f));
                glyph.rectTransform.pivot = new Vector2(0.5f, 0.5f);
                name = Lbl(r, "", 54, Pal.Text, 116f, 98f, 700f, 70f, FontStyle.Bold);
                desc = UiKit.Label(r, "", 24, Pal.TextDim, new Vector2(0f, 1f), new Vector2(34f, -140f), new Vector2(W - 68f, 70f), TextAnchor.UpperLeft, FontStyle.Normal);
                desc.rectTransform.pivot = new Vector2(0f, 1f);
                traits = UiKit.Label(r, "", 25, Pal.Text, new Vector2(0f, 1f), new Vector2(34f, -212f), new Vector2(W - 68f, 130f), TextAnchor.UpperLeft, FontStyle.Normal);
                traits.rectTransform.pivot = new Vector2(0f, 1f);
                traits.supportRichText = true;
                traits.lineSpacing = 1.25f;
                souls = Lbl(r, "", 21, Pal.Muted, 34f, 356f, 160f, 30f);
                for (int k = 0; k < cells.Length; k++)
                    cells[k] = UiKit.Image(r, "Cell", Color.white, new Vector2(0f, 1f), new Vector2(150f + k * 30f, -356f), new Vector2(18f, 18f));
            }

            public void SetActive(bool v) { if (root.activeSelf != v) root.SetActive(v); }

            public void Show(SkillCard c, string cap, Color capColor, Color frameColor)
            {
                SetActive(true);
                frame.color = frameColor;
                var rar = Art.RarityColor(c.Rarity);
                caption.text = $"{cap}  ·  <color=#{Hex(rar)}>{Rarities.Name(c.Rarity).ToUpperInvariant()}</color>";
                caption.color = capColor;
                glyph.text = FormText.Glyph(c.Form); glyph.color = Art.CardTint(c); glyph.enabled = true;
                name.text = FormText.Name(c.Form);
                name.rectTransform.anchoredPosition = new Vector2(116f, -98f);
                desc.text = FormText.Base(c.Form);
                var sb = new System.Text.StringBuilder();
                foreach (var t in c.Traits)
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append($"<color=#{Hex(Art.TraitColor(t))}>◆ {FormText.Name(t)}{(c.Level(t) > 1 ? $" ×{c.Level(t)}" : "")}</color> — {FormText.Cell(t, c.Form)}");
                }
                traits.text = sb.ToString();
                souls.text = $"Души {c.Souls.Count}/{c.SoulCells}";
                for (int k = 0; k < cells.Length; k++)
                {
                    cells[k].enabled = k < c.SoulCells;
                    cells[k].color = k < c.Souls.Count ? Art.TraitColor(c.Souls[k]) : rar.WithAlpha(0.35f);
                }
            }

            public void ShowEmpty(string cap, string text)
            {
                SetActive(true);
                frame.color = Pal.GoldDim;
                caption.text = cap; caption.color = Pal.Gold;
                glyph.enabled = false;
                name.text = "Слот пуст";
                name.rectTransform.anchoredPosition = new Vector2(34f, -98f);
                desc.text = text;
                traits.text = "";
                souls.text = "";
                foreach (var c in cells) c.enabled = false;
            }
        }

        // ---------- слот ----------

        sealed class SlotButton
        {
            readonly Image frame, bg, key;
            readonly Text keyText, glyph, name;

            public SlotButton(RewardScreen screen, Transform parent, int index, float x, float y)
            {
                frame = UiKit.Image(parent, "Slot", Pal.Line, new Vector2(0f, 1f), new Vector2(x, -y), new Vector2(372f, 90f), new Vector2(0f, 1f));
                frame.raycastTarget = true;
                var btn = frame.gameObject.AddComponent<Button>();
                btn.transition = Selectable.Transition.None;
                btn.onClick.AddListener(() => screen.focus = index);
                bg = UiKit.Image(frame.transform, "Bg", Pal.Card, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(366f, 84f));
                key = UiKit.Image(frame.transform, "Key", Pal.KeyDark, new Vector2(0f, 0.5f), new Vector2(22f, 0f), new Vector2(72f, 36f), new Vector2(0f, 0.5f));
                keyText = UiKit.Label(key.transform, "", 21, Pal.Gold, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(72f, 36f));
                keyText.GetComponent<Shadow>().enabled = false;
                glyph = UiKit.Label(frame.transform, "", 30, Color.white, new Vector2(0f, 0.5f), new Vector2(146f, 0f), new Vector2(50f, 50f));
                glyph.rectTransform.pivot = new Vector2(0.5f, 0.5f);
                name = UiKit.Label(frame.transform, "", 27, Pal.Text, new Vector2(0f, 0.5f), new Vector2(180f, 0f), new Vector2(180f, 50f), TextAnchor.MiddleLeft, FontStyle.Bold);
                name.rectTransform.pivot = new Vector2(0f, 0.5f);
            }

            public void Sync(SkillCard c, string k, bool focused)
            {
                frame.color = focused ? Pal.Gold : c == null ? new Color(1f, 1f, 1f, 0.12f) : Pal.Line;
                bg.color = focused ? Pal.Focus : Pal.Card;
                if (keyText.text != k)
                {
                    keyText.text = k;
                    key.rectTransform.sizeDelta = new Vector2(Mathf.Max(48f, keyText.preferredWidth + 22f), 36f);
                    keyText.rectTransform.sizeDelta = key.rectTransform.sizeDelta;
                }
                glyph.enabled = c != null;
                if (c != null) { glyph.text = FormText.Glyph(c.Form); glyph.color = Art.CardTint(c); }
                name.text = c != null ? FormText.Name(c.Form) : "пусто";
                name.color = c != null ? Pal.Text : Pal.Faint;
                name.fontStyle = c != null ? FontStyle.Bold : FontStyle.Normal;
            }
        }

        // ---------- действие ----------

        sealed class ActionButton
        {
            readonly Image frame, bg, badge;
            readonly Text badgeText, title, note;
            readonly Color tint, fill;
            bool enabled;
            readonly System.Action click;

            public ActionButton(Transform parent, float x, float y, float w, Color tint, Color fill, System.Action click)
            {
                this.tint = tint; this.fill = fill; this.click = click;
                frame = UiKit.Image(parent, "Action", tint, new Vector2(0f, 1f), new Vector2(x, -y), new Vector2(w, 116f), new Vector2(0f, 1f));
                frame.raycastTarget = true;
                var btn = frame.gameObject.AddComponent<Button>();
                btn.transition = Selectable.Transition.None;
                btn.onClick.AddListener(() => { if (enabled) this.click(); });
                bg = UiKit.Image(frame.transform, "Bg", fill, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(w - 4f, 112f));
                badge = UiKit.Image(frame.transform, "Badge", tint, new Vector2(0f, 0.5f), new Vector2(28f, 0f), new Vector2(52f, 52f), new Vector2(0f, 0.5f));
                badgeText = UiKit.Label(badge.transform, "", 26, fill, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(52f, 52f));
                badgeText.GetComponent<Shadow>().enabled = false;
                title = UiKit.Label(frame.transform, "", 32, tint, new Vector2(0f, 1f), new Vector2(100f, -38f), new Vector2(w - 120f, 44f), TextAnchor.MiddleLeft, FontStyle.Bold);
                title.rectTransform.pivot = new Vector2(0f, 0.5f);
                note = UiKit.Label(frame.transform, "", 22, Pal.Muted, new Vector2(0f, 1f), new Vector2(100f, -78f), new Vector2(w - 120f, 32f), TextAnchor.MiddleLeft, FontStyle.Bold);
                note.rectTransform.pivot = new Vector2(0f, 0.5f);
                note.horizontalOverflow = HorizontalWrapMode.Wrap;
            }

            public void Set(string key, string titleText, string noteText, Color noteColor, bool on)
            {
                enabled = on;
                frame.color = on ? tint : new Color(1f, 1f, 1f, 0.12f);
                bg.color = on ? fill : new Color(0.08f, 0.065f, 0.11f, 0.9f);
                badge.enabled = key.Length > 0;
                badge.color = on ? tint : Pal.KeyDark;
                badgeText.text = key;
                badgeText.color = on ? fill : Pal.Faint;
                float x = key.Length > 0 ? 100f : 32f;
                title.rectTransform.anchoredPosition = new Vector2(x, -38f);
                note.rectTransform.anchoredPosition = new Vector2(x, -78f);
                title.text = titleText;
                title.color = on ? tint : Pal.Faint;
                note.text = noteText;
                note.color = noteColor;
            }
        }
    }
}
