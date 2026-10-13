using Arena.Core;
using Arena.Sim;
using Arena.View;
using UnityEngine;
using UnityEngine.UI;

namespace Arena.UI
{
    /// <summary>Колода (Tab): 4 слота «главная + улучшение» и запас из 11 карт. Клик по карте, потом по месту — поменять местами.</summary>
    public sealed class DeckScreen
    {
        static readonly string[] Keys = { "ЛКМ / X", "ПКМ / Y", "Q / B", "Space / A" };

        readonly Canvas canvas;
        readonly ArenaSim sim;
        readonly CardFace[] faces = new CardFace[19];
        readonly Text info;
        int selected = -1, hover = -1, focus;

        public bool Visible => canvas.enabled;

        public DeckScreen(ArenaSim sim)
        {
            this.sim = sim;
            canvas = UiKit.Canvas("Deck", 20);
            var t = canvas.transform;
            UiKit.Fill(t, "Dim", new Color(0.06f, 0.03f, 0.1f, 0.92f)).raycastTarget = true;
            UiKit.Label(t, "Колода", 44, new Color(1f, 0.85f, 0.45f), new Vector2(0f, 1f), new Vector2(80f, -50f), new Vector2(400f, 60f), TextAnchor.MiddleLeft).rectTransform.pivot = new Vector2(0f, 0.5f);
            var sub = UiKit.Label(t, "Клик по карте, потом по месту — поменять местами. Карта под главной — улучшение: её эффект добавляется к главной, и цвет главной решает, какой.",
                18, new Color(0.85f, 0.8f, 0.95f), new Vector2(0f, 1f), new Vector2(80f, -95f), new Vector2(1250f, 30f), TextAnchor.MiddleLeft, FontStyle.Normal);
            sub.rectTransform.pivot = new Vector2(0f, 0.5f);

            for (int i = 0; i < 4; i++)
            {
                float x = 80f + i * 230f;
                UiKit.Label(t, $"Слот {i + 1}: {Keys[i]}", 18, new Color(1f, 0.85f, 0.45f), new Vector2(0f, 1f), new Vector2(x + 100f, -135f), new Vector2(220f, 30f));
                faces[i] = Place(t, i, new Vector2(x, -155f), new Vector2(200f, 260f));
                faces[4 + i] = Place(t, 4 + i, new Vector2(x, -425f), new Vector2(200f, 110f));
            }
            UiKit.Label(t, "Запас", 22, new Color(1f, 0.85f, 0.45f), new Vector2(0f, 1f), new Vector2(80f, -560f), new Vector2(200f, 30f), TextAnchor.MiddleLeft).rectTransform.pivot = new Vector2(0f, 0.5f);
            for (int i = 0; i < 11; i++)
            {
                int row = i / 6, col = i % 6;
                faces[8 + i] = Place(t, 8 + i, new Vector2(80f + col * 165f, -585f - row * 215f), new Vector2(150f, 200f));
            }

            var panel = UiKit.Image(t, "Info", new Color(0.16f, 0.08f, 0.24f, 0.95f), new Vector2(1f, 1f), new Vector2(-60f, -140f), new Vector2(560f, 860f), new Vector2(1f, 1f));
            info = UiKit.Label(panel.transform, "", 20, Color.white, new Vector2(0.5f, 1f), new Vector2(0f, -20f), new Vector2(510f, 820f), TextAnchor.UpperLeft, FontStyle.Normal);
            info.rectTransform.pivot = new Vector2(0.5f, 1f);
            info.supportRichText = true;
            canvas.enabled = false;
        }

        CardFace Place(Transform parent, int index, Vector2 pos, Vector2 size)
        {
            var face = new CardFace(parent, new Vector2(0f, 1f), pos + new Vector2(size.x * 0.5f, -size.y * 0.5f), size);
            var img = face.Root.gameObject.AddComponent<Image>();
            img.sprite = UiKit.White; img.color = new Color(0f, 0f, 0f, 0f);
            var btn = face.Root.gameObject.AddComponent<Button>();
            btn.transition = Selectable.Transition.None;
            btn.onClick.AddListener(() => Click(index));
            var hov = face.Root.gameObject.AddComponent<HoverRelay>();
            hov.Enter = () => { hover = index; Refresh(); };
            hov.Exit = () => { if (hover == index) hover = -1; Refresh(); };
            return face;
        }

        void Click(int index)
        {
            if (selected < 0) { if (sim.GetPlace(index).HasValue) selected = index; }
            else { if (selected != index) sim.SwapPlaces(selected, index); selected = -1; }
            Refresh();
        }

        public void Show(bool v)
        {
            canvas.enabled = v;
            selected = -1; hover = -1; focus = 0;
            if (v) Refresh();
        }

        /// <summary>Геймпад и стрелки: стик или крестовина — по картам, A — взять карту и поставить на место, B — отменить выбор
        /// или закрыть колоду. Возвращает true, если колоду надо закрыть.</summary>
        public bool Nav(InputReader input)
        {
            if (!Visible) return false;
            if (input.NavX != 0 || input.NavY != 0)
            {
                var (row, col) = Cell(focus);
                row = Mathf.Clamp(row - input.NavY, 0, 3);
                col += input.NavX;
                focus = Index(row, col);
                hover = focus;
                Refresh();
            }
            if (input.NavConfirm) { hover = focus; Click(focus); }
            if (input.NavBack)
            {
                if (selected >= 0) { selected = -1; Refresh(); }
                else return true;
            }
            return false;
        }

        // сетка мест: ряд 0 — главные 0–3, ряд 1 — улучшения 4–7, ряды 2–3 — запас по 6
        static (int row, int col) Cell(int i) => i < 4 ? (0, i) : i < 8 ? (1, i - 4) : i < 14 ? (2, i - 8) : (3, i - 14);

        static int Index(int row, int col) => row switch
        {
            0 => Mathf.Clamp(col, 0, 3),
            1 => 4 + Mathf.Clamp(col, 0, 3),
            2 => 8 + Mathf.Clamp(col, 0, 5),
            _ => 14 + Mathf.Clamp(col, 0, 4),
        };

        void Refresh()
        {
            for (int i = 0; i < faces.Length; i++) faces[i].Set(sim.GetPlace(i), i == selected);
            int shown = hover >= 0 && sim.GetPlace(hover).HasValue ? hover : selected;
            info.text = shown >= 0 ? Describe(shown) : General();
        }

        string Describe(int place)
        {
            var id = sim.GetPlace(place).Value;
            var c = CardCatalog.Get(id);
            string col = ColorUtility.ToHtmlStringRGB(Art.CardColor(c.Color));
            var sb = new System.Text.StringBuilder();
            sb.Append($"<size=30><b><color=#{col}>{c.Name}</color></b></size>\n");
            sb.Append($"<color=#cdbbe0>{ColorName(c.Color)} · {c.Kind}</color>\n\n");
            sb.Append($"<b>Урон:</b> {c.DamageText}   <b>КД:</b> {c.Cooldown:0.##} с{(c.Charges > 1 ? $"   <b>Заряды:</b> {c.Charges}" : "")}\n");
            sb.Append($"Каст {c.Casting:0.###} с · восстановление {c.Recovery:0.##} с\n");
            sb.Append($"Цикл контроля: {c.Cycle} (Stun/Fear {c.CcDuration:0.##} с)\n");
            if (c.Aims) sb.Append($"Автолок: до {c.LockRange:0.#}, ±{c.LockArc:0}°\n");
            sb.Append('\n');
            foreach (var l in c.Lines) if (!string.IsNullOrEmpty(l)) sb.Append(l).Append('\n');
            sb.Append("\n<b><color=#ffd970>Как улучшение:</color></b>\n");
            var onPink = Upgrades.For(id, CardId.TwinDaggers);
            var onYellow = Upgrades.For(id, CardId.BlackHole);
            if (onPink.Name == onYellow.Name) sb.Append(onYellow.Text.Replace("(по циклу главной карты)", "")).Append('\n');
            else
            {
                sb.Append($"<color=#ffe98a>На жёлтой и синей:</color> {onYellow.Name} — длительность по циклу главной\n");
                sb.Append($"<color=#ff9cf6>На розовой:</color> {onPink.Text}\n");
            }
            if (place < 8)
            {
                var s = sim.Slots[place % 4];
                sb.Append($"\n<b><color=#ffd970>Слот {place % 4 + 1} сейчас:</color></b>\n");
                sb.Append(s.Main.HasValue ? CardCatalog.Get(s.Main.Value).Name : "— нет главной карты —").Append('\n');
                var up = s.Upgrade;
                if (!up.IsNone) sb.Append($"<color=#9ff>+ {up.Text}</color>\n");
            }
            return sb.ToString();
        }

        static string General() =>
            "<size=26><b>Как работают карты</b></size>\n\n" +
            "В каждом слоте — главная карта и карта-улучшение под ней.\n\n" +
            "Цвет главной карты решает, что даст улучшение:\n" +
            "• Dazzling Display — Stun на жёлтой и синей, Prey Seeker на розовой;\n" +
            "• Black Hole — Pull или Cull the Meek;\n" +
            "• Jump Scare — Fear или Taste of Fear.\n\n" +
            "Длительность Stun и Fear берётся из «цикла» главной карты: чем длиннее её перезарядка, тем дольше контроль.\n\n" +
            "Розовые — атака: короткое нажатие. Жёлтые — области и защита: держите кнопку, ведите прицел, отпустите. " +
            "Синие — рывки и телепорты, срабатывают сразу и прерывают восстановление после других карт.\n\n" +
            "Наведите на карту (геймпад: стик, A — выбрать, B — назад), чтобы увидеть описание.";

        static string ColorName(CardColor c) => c switch { CardColor.Pink => "Розовая", CardColor.Yellow => "Жёлтая", _ => "Синяя" };
    }
}
