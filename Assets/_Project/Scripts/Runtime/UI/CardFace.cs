using Arena.Core;
using Arena.View;
using UnityEngine;
using UnityEngine.UI;

namespace Arena.UI
{
    /// <summary>Лицо карты: цветная шапка с именем, тип, урон и перезарядка.</summary>
    public sealed class CardFace
    {
        public readonly RectTransform Root;
        readonly Image bg, header, frame;
        readonly Text title, kind, dmg, cd, glyph;

        public CardFace(Transform parent, Vector2 anchor, Vector2 pos, Vector2 size)
        {
            Root = UiKit.Rect(parent, "Card", anchor, pos, size);
            frame = UiKit.Fill(Root, "Frame", Color.white);
            var inner = UiKit.Stretch(Root, "Inner");
            inner.offsetMin = new Vector2(4f, 4f); inner.offsetMax = new Vector2(-4f, -4f);
            bg = UiKit.Fill(inner, "Bg", new Color(0.13f, 0.07f, 0.2f));
            header = UiKit.Image(inner, "Header", Color.white, new Vector2(0.5f, 1f), Vector2.zero, new Vector2(size.x - 8f, Mathf.Max(26f, size.y * 0.15f)));
            float big = Mathf.Clamp(size.x * 0.085f, 12f, 18f);
            title = UiKit.Label(header.transform, "", Mathf.RoundToInt(big), new Color(0.1f, 0.05f, 0.15f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(size.x - 12f, 40f));
            title.GetComponent<Shadow>().enabled = false;
            glyph = UiKit.Label(inner, "", Mathf.RoundToInt(size.x * 0.32f), Color.white, new Vector2(0.5f, 0.55f), Vector2.zero, new Vector2(size.x, size.x * 0.4f));
            kind = UiKit.Label(inner, "", Mathf.RoundToInt(big * 0.85f), new Color(0.8f, 0.75f, 0.9f), new Vector2(0.5f, 0f), new Vector2(0f, size.y * 0.22f), new Vector2(size.x - 10f, 24f), TextAnchor.MiddleCenter, FontStyle.Normal);
            dmg = UiKit.Label(inner, "", Mathf.RoundToInt(big), Color.white, new Vector2(0f, 0f), new Vector2(8f, 16f), new Vector2(size.x * 0.5f, 24f), TextAnchor.MiddleLeft);
            dmg.rectTransform.pivot = new Vector2(0f, 0.5f);
            cd = UiKit.Label(inner, "", Mathf.RoundToInt(big * 0.8f), new Color(0.8f, 0.75f, 0.9f), new Vector2(1f, 0f), new Vector2(-8f, 16f), new Vector2(size.x * 0.5f, 24f), TextAnchor.MiddleRight, FontStyle.Normal);
            cd.rectTransform.pivot = new Vector2(1f, 0.5f);
        }

        public void Set(CardId? id, bool selected = false)
        {
            bool has = id.HasValue;
            header.gameObject.SetActive(has);
            glyph.gameObject.SetActive(has);
            kind.gameObject.SetActive(has); dmg.gameObject.SetActive(has); cd.gameObject.SetActive(has);
            if (!has)
            {
                frame.color = selected ? Color.white : new Color(1f, 1f, 1f, 0.15f);
                bg.color = new Color(0.1f, 0.05f, 0.15f, 0.6f);
                return;
            }
            var c = CardCatalog.Get(id.Value);
            var col = Art.CardColor(c.Color);
            frame.color = selected ? Color.white : col;
            bg.color = new Color(0.13f, 0.07f, 0.2f);
            header.color = col;
            title.text = c.Name;
            glyph.text = Glyph(c.Id);
            glyph.color = col;
            kind.text = c.Kind;
            dmg.text = c.DamageText;
            cd.text = $"КД {c.Cooldown:0.##}с" + (c.Charges > 1 ? $" ×{c.Charges}" : "");
        }

        static string Glyph(CardId id) => id switch
        {
            CardId.TwinDaggers => "††", CardId.FiveOfAKind => "♠", CardId.Crescendo => "✸", CardId.Roses => "✿", CardId.Dio => "ᴥ",
            CardId.BlackHole => "◉", CardId.Dazzling => "☀", CardId.Starburst => "✦", CardId.Aegis => "◈", CardId.MasterfulJump => "⤴",
            CardId.Dash => "»", CardId.JumpScare => "☠", CardId.BlazingSprint => "♨", CardId.ShockingArrival => "ϟ", _ => "☁",
        };
    }
}
