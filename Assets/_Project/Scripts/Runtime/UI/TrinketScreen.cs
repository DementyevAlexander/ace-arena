using Arena.Core;
using Arena.Sim;
using Arena.View;
using UnityEngine;
using UnityEngine.UI;

namespace Arena.UI
{
    /// <summary>Выбор тринкета, подобранного с пятачка (как экран карты): сверху полученный тринкет — категория, что даёт
    /// на своём уровне и особый эффект III; ниже три слота и действие. Такой же уже стоит — «Поднять до …»; иначе
    /// «Поставить» в пустой слот или «Заменить» (прежний пропадёт вместе с уровнем). Слот — ← → или клик;
    /// A / Enter — действие; B / Esc — оставить на земле.</summary>
    public sealed class TrinketScreen
    {
        static readonly Color Got = new(1f, 0.6f, 0.2f);
        static readonly Color ActTint = new(0.72f, 0.58f, 1f);
        static readonly Color ActFill = new(0.17f, 0.12f, 0.26f);
        static readonly Color Loss = new(1f, 0.47f, 0.42f);
        static readonly Color Gain = new(1f, 0.85f, 0.35f);
        const float InputDelay = 0.35f; // «взять», зажатое в бою, не должно сразу нажать кнопку

        readonly Canvas canvas;
        readonly ArenaSim sim;
        readonly Image frame;
        readonly Text name, category, bonus, special, hint, leave;
        readonly SlotButton[] slots = new SlotButton[TrinketBook.Slots];
        readonly Image actFrame, actBg, actBadge;
        readonly Text actBadgeText, actTitle, actNote;
        int focus;
        bool actOn;
        float openedAt;

        public bool Visible => canvas.enabled;

        public TrinketScreen(ArenaSim sim)
        {
            this.sim = sim;
            canvas = UiKit.Canvas("Trinket", 25);
            UiKit.Fill(canvas.transform, "Dim", new Color(0.03f, 0.02f, 0.05f, 0.72f)).raycastTarget = true;
            var t = UiKit.Rect(canvas.transform, "Layout", new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1920f, 1080f));

            // полученный тринкет
            var card = UiKit.Rect(t, "Got", new Vector2(0f, 1f), new Vector2(360f, -150f), new Vector2(1200f, 380f), new Vector2(0f, 1f));
            frame = UiKit.Image(card, "Frame", Got, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1200f, 380f));
            UiKit.Image(card, "Bg", new Color(0.09f, 0.07f, 0.13f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1196f, 376f));
            Lbl(card, "ПОЛУЧЕН ТРИНКЕТ", 22, Got, 34f, 42f, 700f, 30f, FontStyle.Bold);
            name = Lbl(card, "", 54, Pal.Text, 34f, 100f, 1100f, 70f, FontStyle.Bold);
            category = Lbl(card, "", 26, Pal.Muted, 34f, 152f, 1100f, 36f);
            bonus = Lbl(card, "", 30, Pal.Text, 34f, 214f, 1100f, 40f);
            special = Lbl(card, "", 24, Pal.TextDim, 34f, 266f, 1100f, 36f);
            hint = Lbl(card, "", 22, Pal.Faint, 34f, 330f, 1100f, 32f);

            // слоты и действие
            var box = UiKit.Image(t, "Box", Pal.Line, new Vector2(0f, 1f), new Vector2(360f, -570f), new Vector2(1200f, 290f), new Vector2(0f, 1f));
            UiKit.Image(box.transform, "Bg", new Color(0.07f, 0.055f, 0.1f, 0.92f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1196f, 286f));
            Lbl(box.transform, "СЛОТ", 22, Pal.Muted, 30f, 62f, 200f, 30f);
            for (int i = 0; i < slots.Length; i++) slots[i] = new SlotButton(this, box.transform, i, 150f + i * 345f, 22f);
            actFrame = UiKit.Image(box.transform, "Action", ActTint, new Vector2(0f, 1f), new Vector2(150f, -146f), new Vector2(1020f, 116f), new Vector2(0f, 1f));
            actFrame.raycastTarget = true;
            var btn = actFrame.gameObject.AddComponent<Button>();
            btn.transition = Selectable.Transition.None;
            btn.onClick.AddListener(Act);
            actBg = UiKit.Image(actFrame.transform, "Bg", ActFill, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1016f, 112f));
            actBadge = UiKit.Image(actFrame.transform, "Badge", ActTint, new Vector2(0f, 0.5f), new Vector2(28f, 0f), new Vector2(52f, 52f), new Vector2(0f, 0.5f));
            actBadgeText = UiKit.Label(actBadge.transform, "", 26, ActFill, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(52f, 52f));
            actBadgeText.GetComponent<Shadow>().enabled = false;
            actTitle = UiKit.Label(actFrame.transform, "", 32, ActTint, new Vector2(0f, 1f), new Vector2(100f, -38f), new Vector2(900f, 44f), TextAnchor.MiddleLeft, FontStyle.Bold);
            actTitle.rectTransform.pivot = new Vector2(0f, 0.5f);
            actNote = UiKit.Label(actFrame.transform, "", 22, Pal.Muted, new Vector2(0f, 1f), new Vector2(100f, -78f), new Vector2(900f, 32f), TextAnchor.MiddleLeft, FontStyle.Bold);
            actNote.rectTransform.pivot = new Vector2(0f, 0.5f);

            leave = UiKit.Label(t, "", 22, Pal.Muted, new Vector2(0f, 1f), new Vector2(960f, -905f), new Vector2(800f, 36f), TextAnchor.MiddleCenter, FontStyle.Normal);
            leave.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            leave.raycastTarget = true;
            var lb = leave.gameObject.AddComponent<Button>();
            lb.transition = Selectable.Transition.None;
            lb.onClick.AddListener(() => sim.LeaveLoot());
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

        public void Sync(InputReader input)
        {
            bool want = sim.RewardPending && sim.TrinketOffer != null;
            if (want != canvas.enabled)
            {
                canvas.enabled = want;
                openedAt = Time.unscaledTime;
                if (want) focus = BestSlot();
            }
            if (!want) return;
            if (Time.unscaledTime - openedAt >= InputDelay)
            {
                if (input.NavX != 0 && !sim.TrinketOfferIsLevelUp) focus = (focus + input.NavX + slots.Length) % slots.Length;
                if (input.NavConfirm) Act();
                else if (input.NavBack || input.PausePressed) sim.LeaveLoot();
            }
            if (!sim.RewardPending || sim.TrinketOffer == null) { canvas.enabled = false; return; }
            Draw(input.UseMouse);
        }

        /// <summary>С какого слота начать: где стоит такой же; иначе первый пустой; иначе первый.</summary>
        int BestSlot()
        {
            int own = sim.TrinketSlotOf(sim.TrinketOffer.Kind);
            if (own >= 0) return own;
            for (int i = 0; i < sim.TrinketSlots.Length; i++) if (sim.TrinketSlots[i] == null) return i;
            return 0;
        }

        void Act()
        {
            if (!actOn) return;
            if (sim.TrinketOfferIsLevelUp) sim.LevelTrinket();
            else sim.PutTrinket(focus);
        }

        void Draw(bool mouse)
        {
            var got = sim.TrinketOffer;
            var col = Art.TrinketColor(got.Kind);
            var cfg = sim.TrinketCfg;
            int own = sim.TrinketSlotOf(got.Kind);
            if (own >= 0) focus = own;
            frame.color = Got;
            name.text = $"{TrinketBook.Name(got.Kind)} <color=#{Hex(col)}>{TrinketBook.Roman(got.Level)}</color>";
            category.text = TrinketBook.Group(got.Kind) switch
            {
                TrinketGroup.Trait => $"Свойство <color=#{Hex(col)}>{TrinketBook.Category(got.Kind)}</color> — на любой карте. Карты с ним выпадают чаще.",
                TrinketGroup.Form => $"Форма <color=#{Hex(col)}>{TrinketBook.Category(got.Kind)}</color> — все карты этой формы. Они выпадают чаще.",
                _ => $"Механика <color=#{Hex(col)}>{TrinketBook.Category(got.Kind)}</color> — все карты.",
            };
            bonus.text = $"<color=#{Hex(col)}>◆</color> {Cap(TrinketBook.Bonus(got.Kind, got.Level, cfg))}";
            special.text = $"III: {TrinketBook.Special(got.Kind)} <color=#{Hex(Pal.Faint)}>(пока не работает)</color>";
            hint.text = "Уровни I–V. Повтор такого же тринкета поднимает его уровень. Выбросить нельзя — только заменить.";
            for (int i = 0; i < slots.Length; i++) slots[i].Sync(sim.TrinketSlots[i], i, i == focus);
            leave.text = mouse ? "Esc — оставить на земле" : "B — оставить на земле";

            string key = mouse ? "" : "A";
            if (sim.TrinketOfferIsLevelUp)
            {
                var cur = sim.TrinketSlots[own];
                if (sim.CanLevelTrinket)
                    SetAct(key, $"Поднять {TrinketBook.Name(got.Kind)} до {TrinketBook.Roman(cur.Level + 1)}",
                        $"▲ {Cap(TrinketBook.Bonus(got.Kind, cur.Level + 1, cfg))}{(cur.Level + 1 == 3 ? " · особый эффект III" : "")}", Gain, true);
                else SetAct(key, $"{TrinketBook.Name(got.Kind)} уже на V", "Выше не поднять", Pal.Faint, false);
            }
            else
            {
                var cur = sim.TrinketSlots[focus];
                if (cur == null) SetAct(key, $"Поставить в слот {focus + 1}", "Ничего не пропадёт", Pal.Muted, sim.CanPutTrinket(focus));
                else SetAct(key, $"Заменить {TrinketBook.Name(cur.Kind)} {TrinketBook.Roman(cur.Level)}",
                    $"− {TrinketBook.Name(cur.Kind)} пропадёт вместе с уровнем", Loss, sim.CanPutTrinket(focus));
            }
        }

        static string Cap(string s) => s.Length > 0 ? char.ToUpper(s[0]) + s.Substring(1) : s;

        void SetAct(string key, string title, string note, Color noteColor, bool on)
        {
            actOn = on;
            actFrame.color = on ? ActTint : new Color(1f, 1f, 1f, 0.12f);
            actBg.color = on ? ActFill : new Color(0.08f, 0.065f, 0.11f, 0.9f);
            actBadge.enabled = key.Length > 0;
            actBadge.color = on ? ActTint : Pal.KeyDark;
            actBadgeText.text = key;
            actBadgeText.color = on ? ActFill : Pal.Faint;
            float x = key.Length > 0 ? 100f : 32f;
            actTitle.rectTransform.anchoredPosition = new Vector2(x, -38f);
            actNote.rectTransform.anchoredPosition = new Vector2(x, -78f);
            actTitle.text = title;
            actTitle.color = on ? ActTint : Pal.Faint;
            actNote.text = note;
            actNote.color = noteColor;
        }

        sealed class SlotButton
        {
            readonly Image frame, bg;
            readonly Text name;

            public SlotButton(TrinketScreen screen, Transform parent, int index, float x, float y)
            {
                frame = UiKit.Image(parent, "Slot", Pal.Line, new Vector2(0f, 1f), new Vector2(x, -y), new Vector2(330f, 90f), new Vector2(0f, 1f));
                frame.raycastTarget = true;
                var btn = frame.gameObject.AddComponent<Button>();
                btn.transition = Selectable.Transition.None;
                btn.onClick.AddListener(() => { if (!screen.sim.TrinketOfferIsLevelUp) screen.focus = index; });
                bg = UiKit.Image(frame.transform, "Bg", Pal.Card, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(324f, 84f));
                name = UiKit.Label(frame.transform, "", 26, Pal.Text, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(310f, 80f), TextAnchor.MiddleCenter, FontStyle.Bold);
                name.supportRichText = true;
            }

            public void Sync(Trinket t, int index, bool focused)
            {
                frame.color = focused ? Pal.Gold : t == null ? new Color(1f, 1f, 1f, 0.12f) : Pal.Line;
                bg.color = focused ? Pal.Focus : Pal.Card;
                name.text = t == null
                    ? $"<color=#{Hex(Pal.Faint)}>{index + 1} · пусто</color>"
                    : $"{TrinketBook.Name(t.Kind)} <color=#{Hex(Art.TrinketColor(t.Kind))}>{TrinketBook.Roman(t.Level)}</color>";
            }
        }
    }
}
