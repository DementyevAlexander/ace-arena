using UnityEngine;
using UnityEngine.UI;

namespace Arena.UI
{
    /// <summary>Перелистывание наград на экране выбора: пульсирующие стрелки по краям экрана (клик), под ними клавиши
    /// (Q / E или LB / RB), внизу — «1 / 2». Видны, только если на пятачке больше одной награды.</summary>
    public sealed class PagerArrows
    {
        readonly RectTransform root, left, right;
        readonly Text leftKey, rightKey, counter;
        readonly Text leftGlyph, rightGlyph;

        /// <summary>y — центр стрелок от верха макета 1920×1080; page(−1 / +1) — перелистнуть.</summary>
        public PagerArrows(Transform layout, float y, System.Action<int> page)
        {
            root = UiKit.Rect(layout, "Pager", new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1920f, 1080f));
            (left, leftGlyph, leftKey) = Arrow(root, "‹", new Vector2(0f, 1f), new Vector2(44f, -y), () => page(-1));
            (right, rightGlyph, rightKey) = Arrow(root, "›", new Vector2(0f, 1f), new Vector2(1876f, -y), () => page(1));
            counter = UiKit.Label(root, "", 24, Pal.Muted, new Vector2(0f, 1f), new Vector2(960f, -1000f), new Vector2(300f, 34f));
            counter.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            root.gameObject.SetActive(false);
        }

        static (RectTransform, Text, Text) Arrow(Transform parent, string glyph, Vector2 anchor, Vector2 pos, System.Action click)
        {
            var r = UiKit.Rect(parent, "Arrow", anchor, pos, new Vector2(90f, 170f), new Vector2(0.5f, 0.5f));
            var hit = r.gameObject.AddComponent<Image>();
            hit.color = new Color(0f, 0f, 0f, 0f); // невидимая, но кликается
            var btn = r.gameObject.AddComponent<Button>();
            btn.transition = Selectable.Transition.None;
            btn.onClick.AddListener(() => click());
            var g = UiKit.Label(r, glyph, 150, Pal.Gold, new Vector2(0.5f, 0.5f), new Vector2(0f, 12f), new Vector2(90f, 170f));
            g.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            var k = UiKit.Label(r, "", 20, Pal.Muted, new Vector2(0.5f, 0f), new Vector2(0f, -8f), new Vector2(90f, 28f));
            k.rectTransform.pivot = new Vector2(0.5f, 1f);
            return (r, g, k);
        }

        public void Sync(bool visible, int index, int count, bool pad)
        {
            bool on = visible && count > 1 && index >= 0;
            if (root.gameObject.activeSelf != on) root.gameObject.SetActive(on);
            if (!on) return;
            // пульс: стрелки дышат размером и яркостью
            float k = 0.5f + 0.5f * Mathf.Sin(Time.unscaledTime * 4f);
            float s = 1f + 0.12f * k;
            left.localScale = right.localScale = new Vector3(s, s, 1f);
            var col = Color.Lerp(Pal.GoldDim, Pal.Gold, k);
            leftGlyph.color = rightGlyph.color = col;
            leftKey.text = pad ? "LB" : "Q";
            rightKey.text = pad ? "RB" : "E";
            counter.text = $"{index + 1} / {count}";
        }
    }
}
