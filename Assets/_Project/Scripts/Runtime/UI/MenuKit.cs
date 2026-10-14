using System.Collections.Generic;
using Arena.View;
using UnityEngine;
using UnityEngine.UI;

namespace Arena.UI
{
    /// <summary>Палитра меню в духе Hades: золото по тёмно-лиловому.</summary>
    public static class Pal
    {
        public static readonly Color Gold = new(0.914f, 0.788f, 0.478f);
        public static readonly Color GoldDim = new(0.54f, 0.45f, 0.26f);
        public static readonly Color Ink = new(0.17f, 0.11f, 0.02f);   // текст на золоте
        public static readonly Color Text = new(0.91f, 0.87f, 0.78f);
        public static readonly Color TextDim = new(0.78f, 0.75f, 0.66f);
        public static readonly Color Muted = new(0.66f, 0.62f, 0.77f);
        public static readonly Color Faint = new(0.44f, 0.40f, 0.53f);
        public static readonly Color Danger = new(0.91f, 0.54f, 0.54f);
        public static readonly Color Panel = new(0.086f, 0.067f, 0.12f, 0.96f);
        public static readonly Color Card = new(0.106f, 0.082f, 0.15f, 0.95f);
        public static readonly Color Focus = new(0.17f, 0.13f, 0.22f);
        public static readonly Color KeyDark = new(0.23f, 0.19f, 0.31f);
        public static readonly Color Line = new(0.165f, 0.137f, 0.22f);
    }

    /// <summary>Вертикальное меню как в Hades: у пункта в фокусе — ромб слева и золотая рамка.
    /// Мышь — наведение и клик, клавиатура и геймпад — вверх/вниз, A или Enter — выбрать.</summary>
    public sealed class MenuList
    {
        const float RowH = 58f, Indent = 34f;
        readonly RectTransform root;
        readonly float width;
        readonly List<Item> items = new();
        public int Focus;

        public MenuList(Transform parent, Vector2 pos, float width)
        {
            this.width = width;
            root = UiKit.Rect(parent, "Menu", new Vector2(0f, 1f), pos, new Vector2(width, 0f), new Vector2(0f, 1f));
        }

        public void Add(string label, System.Action click, bool danger = false)
        {
            int i = items.Count;
            var it = new Item { Click = click, Danger = danger };
            var row = UiKit.Rect(root, "Item", new Vector2(0f, 1f), new Vector2(0f, -i * RowH), new Vector2(width, RowH - 8f), new Vector2(0f, 1f));
            it.Mark = UiKit.Image(row, "Mark", Pal.Gold, new Vector2(0f, 0.5f), new Vector2(10f, 0f), new Vector2(13f, 13f));
            it.Mark.rectTransform.localEulerAngles = new Vector3(0f, 0f, 45f);
            it.Frame = UiKit.Image(row, "Frame", Pal.GoldDim, new Vector2(0f, 0.5f), new Vector2(Indent, 0f), new Vector2(width - Indent, RowH - 8f), new Vector2(0f, 0.5f));
            it.Frame.raycastTarget = true;
            it.Bg = UiKit.Image(it.Frame.transform, "Bg", Pal.Focus, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(width - Indent - 4f, RowH - 12f));
            it.Label = UiKit.Label(it.Frame.transform, label, 30, Pal.TextDim, new Vector2(0f, 0.5f), new Vector2(20f, 0f), new Vector2(width - Indent - 30f, RowH - 8f), TextAnchor.MiddleLeft, FontStyle.Normal);
            it.Label.rectTransform.pivot = new Vector2(0f, 0.5f);
            var btn = it.Frame.gameObject.AddComponent<Button>();
            btn.transition = Selectable.Transition.None;
            btn.onClick.AddListener(() => { Focus = i; click(); });
            var hover = it.Frame.gameObject.AddComponent<HoverRelay>();
            hover.Enter = () => Focus = i;
            items.Add(it);
        }

        /// <summary>Вверх/вниз — по кругу, A / Enter — выбрать пункт в фокусе. Возвращает true, если пункт выбран.</summary>
        public bool Navigate(InputReader input)
        {
            if (input.NavY != 0) Focus = (Focus - input.NavY + items.Count) % items.Count;
            if (!input.NavConfirm) return false;
            items[Focus].Click();
            return true;
        }

        public void Sync()
        {
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                bool on = i == Focus;
                it.Mark.enabled = on;
                it.Frame.color = on ? Pal.GoldDim : Color.clear;
                it.Bg.enabled = on;
                it.Label.color = it.Danger ? Pal.Danger : on ? Color.white : Pal.TextDim;
            }
        }

        sealed class Item
        {
            public Image Mark, Frame, Bg;
            public Text Label;
            public System.Action Click;
            public bool Danger;
        }
    }

    /// <summary>Строка подсказок: клавиша на плашке и подпись. Перестраивается, только когда текст меняется.</summary>
    public sealed class HintBar
    {
        readonly RectTransform root;
        readonly List<GameObject> parts = new();
        string shown;

        public HintBar(Transform parent, Vector2 anchor, Vector2 pos)
        {
            root = UiKit.Rect(parent, "Hints", anchor, pos, new Vector2(10f, 34f), anchor);
        }

        /// <summary>Пары «клавиша, подпись»; клавиша, начинающаяся с «!», — тёмная плашка (второстепенное действие).</summary>
        public void Set(params string[] keyLabel)
        {
            string key = string.Join("|", keyLabel);
            if (key == shown) return;
            shown = key;
            foreach (var p in parts) Object.Destroy(p);
            parts.Clear();
            float x = 0f;
            for (int i = 0; i + 1 < keyLabel.Length; i += 2)
            {
                string k = keyLabel[i];
                bool dark = k.StartsWith("!");
                if (dark) k = k.Substring(1);
                if (k.Length > 0)
                {
                    var probe = UiKit.Label(root, k, 20, dark ? Pal.Text : Pal.Ink, new Vector2(0f, 0.5f), Vector2.zero, new Vector2(400f, 30f), TextAnchor.MiddleCenter);
                    float w = probe.preferredWidth + 18f;
                    var badge = UiKit.Image(root, "Key", dark ? Pal.KeyDark : Pal.Gold, new Vector2(0f, 0.5f), new Vector2(x, 0f), new Vector2(w, 30f), new Vector2(0f, 0.5f));
                    probe.rectTransform.SetParent(badge.transform, false);
                    probe.rectTransform.anchorMin = probe.rectTransform.anchorMax = probe.rectTransform.pivot = new Vector2(0.5f, 0.5f);
                    probe.rectTransform.anchoredPosition = Vector2.zero;
                    probe.rectTransform.sizeDelta = new Vector2(w, 30f);
                    probe.GetComponent<Shadow>().enabled = false;
                    parts.Add(badge.gameObject);
                    x += w + 8f;
                }
                var label = UiKit.Label(root, keyLabel[i + 1], 20, Pal.Muted, new Vector2(0f, 0.5f), new Vector2(x, 0f), new Vector2(600f, 30f), TextAnchor.MiddleLeft, FontStyle.Normal);
                label.rectTransform.pivot = new Vector2(0f, 0.5f);
                float lw = label.preferredWidth;
                label.rectTransform.sizeDelta = new Vector2(lw + 4f, 30f);
                parts.Add(label.gameObject);
                x += lw + 32f;
            }
            root.sizeDelta = new Vector2(x, 34f);
        }
    }

    /// <summary>Панель «Управление»: клавиатура и мышь / геймпад — общая для стартового экрана и паузы.</summary>
    public static class ControlsPanel
    {
        static readonly string[,] Rows =
        {
            { "Бег", "WASD", "Левый стик, крестовина" },
            { "Скиллы слотов 1–3", "ЛКМ / ПКМ / Q", "X / Y / B" },
            { "Снаряд очередью", "держать кнопку", "держать кнопку" },
            { "Рывок", "Space или LeftShift", "A" },
            { "Взять карточку", "E", "RB" },
            { "Скиллы (просмотр)", "Tab", "View" },
            { "Пауза", "Esc", "Menu" },
            { "Заново", "R", "Y в паузе" },
        };

        public static RectTransform Build(Transform parent, Vector2 anchor, Vector2 pos, Vector2 size)
        {
            var root = UiKit.Rect(parent, "Controls", anchor, pos, size, anchor);
            UiKit.Image(root, "Frame", Pal.GoldDim, new Vector2(0.5f, 0.5f), Vector2.zero, size);
            UiKit.Image(root, "Bg", Pal.Panel, new Vector2(0.5f, 0.5f), Vector2.zero, size - new Vector2(4f, 4f));
            var title = UiKit.Label(root, "Управление", 40, Pal.Gold, new Vector2(0f, 1f), new Vector2(40f, -50f), new Vector2(600f, 50f), TextAnchor.MiddleLeft, FontStyle.Normal);
            title.rectTransform.pivot = new Vector2(0f, 0.5f);
            float c1 = 40f, c2 = size.x * 0.40f, c3 = size.x * 0.68f;
            Head(root, "Клавиатура и мышь", c2);
            Head(root, "Геймпад", c3);
            for (int r = 0; r < Rows.GetLength(0); r++)
            {
                float y = -150f - r * 52f;
                UiKit.Image(root, "Line", Pal.Line, new Vector2(0f, 1f), new Vector2(c1, y - 26f), new Vector2(size.x - 80f, 2f), new Vector2(0f, 0.5f));
                Cell(root, Rows[r, 0], c1, y, Pal.TextDim);
                Cell(root, Rows[r, 1], c2, y, Color.white);
                Cell(root, Rows[r, 2], c3, y, Color.white);
            }
            return root;
        }

        static void Head(Transform root, string text, float x)
        {
            var t = UiKit.Label(root, text, 20, Pal.Muted, new Vector2(0f, 1f), new Vector2(x, -104f), new Vector2(320f, 30f), TextAnchor.MiddleLeft, FontStyle.Normal);
            t.rectTransform.pivot = new Vector2(0f, 0.5f);
        }

        static void Cell(Transform root, string text, float x, float y, Color color)
        {
            var t = UiKit.Label(root, text, 23, color, new Vector2(0f, 1f), new Vector2(x, y), new Vector2(320f, 40f), TextAnchor.MiddleLeft, FontStyle.Normal);
            t.rectTransform.pivot = new Vector2(0f, 0.5f);
        }
    }
}
