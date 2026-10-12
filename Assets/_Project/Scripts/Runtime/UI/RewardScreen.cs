using System.Collections.Generic;
using Arena.Core;
using Arena.Sim;
using Arena.View;
using UnityEngine;
using UnityEngine.UI;

namespace Arena.UI
{
    /// <summary>Выбор карточки (после каждой второй волны) и просмотр скиллов (Tab). Сверху — три карточки «форма + свойство»,
    /// снизу — четыре слота. Выбрал карточку — у каждого слота две кнопки: поставить её в слот (прежняя карта пропадёт)
    /// или влить её душу — свойство перейдёт к карте слота, форма останется от неё.</summary>
    public sealed class RewardScreen
    {
        static readonly string[] Keys = { "ЛКМ / X", "ПКМ / Y", "Q / B", "Space / A" };

        readonly Canvas canvas;
        readonly ArenaSim sim;
        readonly Text title, sub, info;
        readonly OfferPanel[] offers = new OfferPanel[3];
        readonly SlotPanel[] slots = new SlotPanel[4];
        int selected = -1;
        string hoverText;
        bool viewOnly;

        public bool Visible => canvas.enabled;

        public RewardScreen(ArenaSim sim)
        {
            this.sim = sim;
            canvas = UiKit.Canvas("Reward", 25);
            var t = canvas.transform;
            UiKit.Fill(t, "Dim", new Color(0.04f, 0.02f, 0.07f, 0.9f)).raycastTarget = true;
            title = UiKit.Label(t, "", 44, new Color(1f, 0.85f, 0.45f), new Vector2(0.5f, 1f), new Vector2(0f, -50f), new Vector2(1600f, 60f));
            sub = UiKit.Label(t, "", 19, new Color(0.85f, 0.8f, 0.95f), new Vector2(0.5f, 1f), new Vector2(0f, -92f), new Vector2(1700f, 30f), TextAnchor.MiddleCenter, FontStyle.Normal);
            for (int i = 0; i < 3; i++) offers[i] = new OfferPanel(this, t, i, new Vector2(-470f + i * 470f, -130f));
            for (int i = 0; i < 4; i++) slots[i] = new SlotPanel(this, t, i, new Vector2(-675f + i * 450f, -500f));
            info = UiKit.Label(t, "", 19, new Color(0.75f, 1f, 0.95f), new Vector2(0.5f, 0f), new Vector2(0f, 40f), new Vector2(1700f, 60f), TextAnchor.MiddleCenter, FontStyle.Normal);
            info.supportRichText = true;
            canvas.enabled = false;
        }

        /// <summary>Показывает выбор, пока он не сделан; Tab — просмотр скиллов без выбора.</summary>
        public void Sync(bool viewRequested)
        {
            bool want = sim.RewardPending || viewRequested;
            if (want != canvas.enabled) { canvas.enabled = want; selected = -1; hoverText = null; }
            if (!want) return;
            viewOnly = !sim.RewardPending;
            if (selected >= sim.Offers.Count) selected = -1;
            title.text = viewOnly ? "Скиллы" : sim.Rewards == 0 ? "Первая душа" : "Души павших";
            sub.text = viewOnly
                ? "Tab или Esc — вернуться в бой"
                : "Выбери карточку, затем слот: «Поставить» — карточка займёт слот, «Влить душу» — её свойство перейдёт к карте слота";
            for (int i = 0; i < 3; i++) offers[i].Sync(viewOnly || i >= sim.Offers.Count ? null : sim.Offers[i], i == selected);
            for (int i = 0; i < 4; i++) slots[i].Sync();
            info.text = hoverText ?? (viewOnly ? "" : selected < 0 ? "Наведи на карточку, чтобы прочитать её" : "Наведи на кнопку слота — увидишь, что получится");
        }

        void Select(int i) { if (!viewOnly && i < sim.Offers.Count) selected = selected == i ? -1 : i; }

        void Take(int slot) { if (selected >= 0 && sim.CanTake(selected, slot)) { sim.TakeOffer(selected, slot); selected = -1; hoverText = null; } }

        void Fuse(int slot) { if (selected >= 0 && sim.CanFuse(selected, slot)) { sim.FuseOffer(selected, slot); selected = -1; hoverText = null; } }

        static string Hex(Color c) => ColorUtility.ToHtmlStringRGB(c);

        static string CardText(SkillCard c)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(FormText.Base(c.Form)).Append('\n');
            foreach (var t in c.Traits)
            {
                int lvl = c.Level(t);
                sb.Append($"\n<color=#{Hex(Art.TraitColor(t))}><b>{FormText.Name(t)}{(lvl > 1 ? $" ×{lvl}" : "")}</b></color> — {FormText.Cell(t, c.Form)}");
            }
            return sb.ToString();
        }

        static Button MakeButton(Transform parent, string label, Vector2 anchor, Vector2 pos, Vector2 size, System.Action click, out Text text, out Image img)
        {
            img = UiKit.Image(parent, label, new Color(0.25f, 0.16f, 0.32f), anchor, pos, size);
            img.raycastTarget = true;
            var btn = img.gameObject.AddComponent<Button>();
            btn.transition = Selectable.Transition.None;
            btn.onClick.AddListener(() => click());
            text = UiKit.Label(img.transform, label, 18, Color.white, new Vector2(0.5f, 0.5f), Vector2.zero, size);
            return btn;
        }

        static void Hover(GameObject go, System.Action enter, System.Action exit)
        {
            var h = go.AddComponent<HoverRelay>();
            h.Enter = enter; h.Exit = exit;
        }

        // ---------- карточка ----------

        sealed class OfferPanel
        {
            readonly RewardScreen screen;
            readonly RectTransform root;
            readonly Image frame, header;
            readonly Text glyph, name, body;

            public OfferPanel(RewardScreen screen, Transform parent, int index, Vector2 pos)
            {
                this.screen = screen;
                root = UiKit.Rect(parent, "Offer", new Vector2(0.5f, 1f), pos, new Vector2(430f, 330f), new Vector2(0.5f, 1f));
                frame = UiKit.Image(root, "Frame", Color.white, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(430f, 330f));
                frame.raycastTarget = true;
                UiKit.Image(root, "Bg", new Color(0.11f, 0.06f, 0.16f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(422f, 322f));
                header = UiKit.Image(root, "Header", Color.white, new Vector2(0.5f, 1f), new Vector2(0f, -4f), new Vector2(422f, 52f), new Vector2(0.5f, 1f));
                name = UiKit.Label(header.transform, "", 24, new Color(0.08f, 0.04f, 0.12f), new Vector2(0.5f, 0.5f), new Vector2(20f, 0f), new Vector2(380f, 50f));
                name.GetComponent<Shadow>().enabled = false;
                glyph = UiKit.Label(header.transform, "", 34, new Color(0.08f, 0.04f, 0.12f), new Vector2(0f, 0.5f), new Vector2(30f, 0f), new Vector2(50f, 50f));
                glyph.GetComponent<Shadow>().enabled = false;
                body = UiKit.Label(root, "", 17, new Color(0.9f, 0.88f, 0.95f), new Vector2(0.5f, 1f), new Vector2(0f, -68f), new Vector2(395f, 250f), TextAnchor.UpperLeft, FontStyle.Normal);
                body.rectTransform.pivot = new Vector2(0.5f, 1f);
                body.supportRichText = true;
                var btn = frame.gameObject.AddComponent<Button>();
                btn.transition = Selectable.Transition.None;
                btn.onClick.AddListener(() => screen.Select(index));
            }

            public void Sync(SkillCard c, bool selected)
            {
                root.gameObject.SetActive(c != null);
                if (c == null) return;
                var col = Art.CardTint(c);
                frame.color = selected ? Color.white : col * 0.75f;
                header.color = col;
                name.text = c.Name;
                glyph.text = FormText.Glyph(c.Form);
                var t = c.Own.Value;
                body.text = $"<b>Как скилл</b> — {(c.Form == FormKind.Dash ? "в слот Space" : "в слот ЛКМ, ПКМ или Q")}:\n{CardText(c)}\n\n" +
                            $"<b>Как душа</b> — отдаёт <color=#{Hex(Art.TraitColor(t))}>{FormText.Name(t)}</color> карте слота (действие зависит от её формы).";
            }
        }

        // ---------- слот ----------

        sealed class SlotPanel
        {
            readonly RewardScreen screen;
            readonly int index;
            readonly Image frame;
            readonly Text key, name, body;
            readonly Button take, fuse;
            readonly Text takeText, fuseText;
            readonly Image takeImg, fuseImg;

            public SlotPanel(RewardScreen screen, Transform parent, int index, Vector2 pos)
            {
                this.screen = screen;
                this.index = index;
                var root = UiKit.Rect(parent, "Slot", new Vector2(0.5f, 1f), pos, new Vector2(430f, 420f), new Vector2(0.5f, 1f));
                frame = UiKit.Image(root, "Frame", Color.white, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(430f, 420f));
                UiKit.Image(root, "Bg", new Color(0.09f, 0.05f, 0.13f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(422f, 412f));
                key = UiKit.Label(root, Keys[index], 18, new Color(1f, 0.85f, 0.45f), new Vector2(0.5f, 1f), new Vector2(0f, -20f), new Vector2(400f, 26f));
                name = UiKit.Label(root, "", 23, Color.white, new Vector2(0.5f, 1f), new Vector2(0f, -50f), new Vector2(400f, 30f));
                body = UiKit.Label(root, "", 16, new Color(0.88f, 0.86f, 0.93f), new Vector2(0.5f, 1f), new Vector2(0f, -72f), new Vector2(395f, 250f), TextAnchor.UpperLeft, FontStyle.Normal);
                body.rectTransform.pivot = new Vector2(0.5f, 1f);
                body.supportRichText = true;
                take = MakeButton(root, "Поставить", new Vector2(0.5f, 0f), new Vector2(-105f, 36f), new Vector2(195f, 46f), () => screen.Take(index), out takeText, out takeImg);
                fuse = MakeButton(root, "Влить душу", new Vector2(0.5f, 0f), new Vector2(105f, 36f), new Vector2(195f, 46f), () => screen.Fuse(index), out fuseText, out fuseImg);
                Hover(take.gameObject, () => screen.hoverText = TakePreview(), () => screen.hoverText = null);
                Hover(fuse.gameObject, () => screen.hoverText = FusePreview(), () => screen.hoverText = null);
            }

            string TakePreview()
            {
                var sim = screen.sim;
                int o = screen.selected;
                if (o < 0) return null;
                if (!sim.CanTake(o, index)) return sim.Offers[o].Form == FormKind.Dash ? "Рывок ставится только в слот Space" : "Слот Space — только для рывка";
                var cur = sim.Forms[index].Card;
                return cur == null ? $"«{sim.Offers[o].Name}» займёт пустой слот" : $"«{sim.Offers[o].Name}» заменит «{cur.Name}» — её души пропадут";
            }

            string FusePreview()
            {
                var sim = screen.sim;
                int o = screen.selected;
                if (o < 0) return null;
                var cur = sim.Forms[index].Card;
                if (cur == null) return "Слот пуст — вливать некуда";
                if (!cur.CanFuse) return "Ячейки душ заполнены";
                var t = sim.Offers[o].Own.Value;
                int lvl = cur.Level(t) + 1;
                return $"{FormText.Name(cur.Form)} получит <color=#{Hex(Art.TraitColor(t))}><b>{FormText.Name(t)}{(lvl > 1 ? $" ×{lvl}" : "")}</b></color> — {FormText.Cell(t, cur.Form)}";
            }

            public void Sync()
            {
                var sim = screen.sim;
                var card = sim.Forms[index].Card;
                frame.color = Art.CardTint(card) * (card != null ? 0.7f : 0.25f);
                name.text = card == null ? "— пусто —" : FormText.Name(card.Form);
                body.text = card == null ? (index == DashSlotIndex ? "Сюда — только рывок." : "Сюда можно поставить снаряд, зону, призыв или барьер.")
                    : CardText(card) + $"\n\nДуши: {card.Souls.Count} / {SkillCard.SoulCells}";
                bool choose = !screen.viewOnly && screen.selected >= 0;
                take.gameObject.SetActive(!screen.viewOnly);
                fuse.gameObject.SetActive(!screen.viewOnly);
                bool canTake = choose && sim.CanTake(screen.selected, index);
                bool canFuse = choose && sim.CanFuse(screen.selected, index);
                takeImg.color = canTake ? new Color(0.35f, 0.22f, 0.5f) : new Color(0.16f, 0.12f, 0.2f);
                fuseImg.color = canFuse ? new Color(0.15f, 0.45f, 0.42f) : new Color(0.12f, 0.16f, 0.18f);
                takeText.color = canTake ? Color.white : new Color(1f, 1f, 1f, 0.3f);
                fuseText.color = canFuse ? Color.white : new Color(1f, 1f, 1f, 0.3f);
            }

            const int DashSlotIndex = ArenaSim.DashSlot;
        }
    }
}
