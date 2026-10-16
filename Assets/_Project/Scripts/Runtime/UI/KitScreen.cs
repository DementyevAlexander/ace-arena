using System.Collections.Generic;
using Arena.Core;
using Arena.Sim;
using Arena.View;
using UnityEngine;
using UnityEngine.UI;

namespace Arena.UI
{
    /// <summary>Стартовый набор у Наставника (как выбор снаряжения перед забегом в Nova Drift): форма карты в ЛКМ и её своё
    /// свойство. Выбор сразу ставит карту в ЛКМ — её можно попробовать на мишенях. Строка — ↑ ↓ или клик; значение — ← →
    /// или стрелки по бокам; A / Enter / B / Esc — готово.</summary>
    public sealed class KitScreen
    {
        const float InputDelay = 0.35f; // «взаимодействие», зажатое в хабе, не должно сразу закрыть выбор

        readonly Canvas canvas;
        readonly ArenaSim sim;
        readonly Text name, desc, hint;
        readonly Row[] rows = new Row[2];
        /// <summary>Свойства на выбор: «без свойства» (null) и все свойства.</summary>
        readonly List<Trait?> souls = new() { null };
        int focus;
        float openedAt;

        public bool Visible => canvas.enabled;

        public KitScreen(ArenaSim sim)
        {
            this.sim = sim;
            for (int i = 0; i < Traits.Count; i++) souls.Add((Trait)i);
            canvas = UiKit.Canvas("Kit", 25);
            UiKit.Fill(canvas.transform, "Dim", new Color(0.03f, 0.02f, 0.05f, 0.72f)).raycastTarget = true;
            var t = UiKit.Rect(canvas.transform, "Layout", new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1920f, 1080f));

            // карта, с которой начнётся забег
            var card = UiKit.Rect(t, "Card", new Vector2(0f, 1f), new Vector2(360f, -120f), new Vector2(1200f, 400f), new Vector2(0f, 1f));
            UiKit.Image(card, "Frame", Pal.Gold, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1200f, 400f));
            UiKit.Image(card, "Bg", new Color(0.09f, 0.07f, 0.13f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1196f, 396f));
            Lbl(card, "СТАРТОВЫЙ НАБОР · КАРТА В ЛКМ", 22, Pal.Gold, 34f, 42f, 1000f, 30f, FontStyle.Bold);
            name = Lbl(card, "", 54, Pal.Text, 34f, 100f, 1100f, 70f, FontStyle.Bold);
            desc = UiKit.Label(card, "", 25, Pal.TextDim, new Vector2(0f, 1f), new Vector2(34f, -150f), new Vector2(1130f, 230f), TextAnchor.UpperLeft, FontStyle.Normal);
            desc.rectTransform.pivot = new Vector2(0f, 1f);
            desc.supportRichText = true;
            desc.lineSpacing = 1.2f;

            // две строки выбора
            var box = UiKit.Image(t, "Box", Pal.Line, new Vector2(0f, 1f), new Vector2(360f, -560f), new Vector2(1200f, 250f), new Vector2(0f, 1f));
            UiKit.Image(box.transform, "Bg", new Color(0.07f, 0.055f, 0.1f, 0.92f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1196f, 246f));
            rows[0] = new Row(this, box.transform, 0, "ФОРМА", 22f);
            rows[1] = new Row(this, box.transform, 1, "СВОЁ СВОЙСТВО", 132f);

            hint = UiKit.Label(t, "", 22, Pal.Muted, new Vector2(0f, 1f), new Vector2(960f, -870f), new Vector2(1200f, 36f), TextAnchor.MiddleCenter, FontStyle.Normal);
            hint.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            hint.raycastTarget = true;
            var hb = hint.gameObject.AddComponent<Button>();
            hb.transition = Selectable.Transition.None;
            hb.onClick.AddListener(() => sim.CloseKit());
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
            bool want = sim.KitOpen;
            if (want != canvas.enabled)
            {
                canvas.enabled = want;
                openedAt = Time.unscaledTime;
                focus = 0;
            }
            if (!want) return;
            if (Time.unscaledTime - openedAt >= InputDelay)
            {
                if (input.NavY != 0) focus = (focus + 1) % rows.Length;
                if (input.NavX != 0) Step(focus, input.NavX);
                if (input.NavConfirm || input.NavBack || input.PausePressed) sim.CloseKit();
            }
            if (!sim.KitOpen) { canvas.enabled = false; return; }
            Draw(input.UseMouse);
        }

        /// <summary>Сдвинуть значение строки на dir (по кругу).</summary>
        void Step(int row, int dir)
        {
            var kit = sim.Kit;
            if (row == 0)
            {
                int i = System.Array.IndexOf(StartKit.Mains, kit.Main);
                sim.SetKit(StartKit.Mains[(i + dir + StartKit.Mains.Length) % StartKit.Mains.Length], kit.Soul);
            }
            else
            {
                int i = souls.IndexOf(kit.Soul);
                sim.SetKit(kit.Main, souls[(i + dir + souls.Count) % souls.Count]);
            }
        }

        void Draw(bool mouse)
        {
            var c = sim.Kit.Card();
            name.text = c.Own.HasValue
                ? $"{FormText.Glyph(c.Form)} {FormText.Name(c.Form)} · <color=#{Hex(Art.TraitColor(c.Own.Value))}>{FormText.Name(c.Own.Value)}</color>"
                : $"{FormText.Glyph(c.Form)} {FormText.Name(c.Form)}";
            var sb = new System.Text.StringBuilder(FormText.Base(c.Form));
            if (c.Own.HasValue) sb.Append($"\n<color=#{Hex(Art.TraitColor(c.Own.Value))}>◆ {FormText.Name(c.Own.Value)}</color> — {FormText.Cell(c.Own.Value, c.Form)}");
            sb.Append($"\n<color=#{Hex(Pal.Faint)}>Рывок в Space — без свойства. Редкая: 2 ячейки под души.</color>");
            desc.text = sb.ToString();
            rows[0].Sync(FormText.Name(sim.Kit.Main), Pal.Text, focus == 0);
            var soul = sim.Kit.Soul;
            rows[1].Sync(soul.HasValue ? FormText.Name(soul.Value) : "без свойства", soul.HasValue ? Art.TraitColor(soul.Value) : Pal.Faint, focus == 1);
            hint.text = mouse ? "← → — менять · ↑ ↓ — строка · Enter или Esc — готово" : "← → — менять · ↑ ↓ — строка · A или B — готово";
        }

        sealed class Row
        {
            readonly Image frame, bg;
            readonly Text value;

            public Row(KitScreen screen, Transform parent, int index, string caption, float y)
            {
                frame = UiKit.Image(parent, "Row", Pal.Line, new Vector2(0f, 1f), new Vector2(30f, -y), new Vector2(1140f, 96f), new Vector2(0f, 1f));
                frame.raycastTarget = true;
                var fb = frame.gameObject.AddComponent<Button>();
                fb.transition = Selectable.Transition.None;
                fb.onClick.AddListener(() => screen.focus = index);
                bg = UiKit.Image(frame.transform, "Bg", Pal.Card, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1134f, 90f));
                var cap = UiKit.Label(frame.transform, caption, 22, Pal.Muted, new Vector2(0f, 0.5f), new Vector2(30f, 0f), new Vector2(300f, 40f), TextAnchor.MiddleLeft, FontStyle.Bold);
                cap.rectTransform.pivot = new Vector2(0f, 0.5f);
                Arrow(screen, frame.transform, index, "◀", 420f, -1);
                Arrow(screen, frame.transform, index, "▶", 1040f, 1);
                value = UiKit.Label(frame.transform, "", 34, Pal.Text, new Vector2(0f, 0.5f), new Vector2(510f, 0f), new Vector2(520f, 60f), TextAnchor.MiddleCenter, FontStyle.Bold);
            }

            static void Arrow(KitScreen screen, Transform parent, int index, string glyph, float x, int dir)
            {
                var a = UiKit.Label(parent, glyph, 34, Pal.Gold, new Vector2(0f, 0.5f), new Vector2(x, 0f), new Vector2(80f, 80f));
                a.raycastTarget = true;
                var b = a.gameObject.AddComponent<Button>();
                b.transition = Selectable.Transition.None;
                b.onClick.AddListener(() => { screen.focus = index; screen.Step(index, dir); });
            }

            public void Sync(string text, Color color, bool focused)
            {
                frame.color = focused ? Pal.Gold : Pal.Line;
                bg.color = focused ? Pal.Focus : Pal.Card;
                value.text = text;
                value.color = color;
            }
        }
    }
}
