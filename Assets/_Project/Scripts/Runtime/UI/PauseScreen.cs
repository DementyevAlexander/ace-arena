using Arena.Core;
using Arena.Sim;
using Arena.View;
using UnityEngine;
using UnityEngine.UI;

namespace Arena.UI
{
    /// <summary>Пауза в духе Hades: арена за затемнением, слева меню (продолжить, скиллы, управление, заново, выйти в меню),
    /// справа — сводка забега и скиллы слотов; «Управление» показывает раскладку на месте сводки.
    /// Закрыть — Esc, Menu или B; Y — заново (это обрабатывает GameController).</summary>
    public sealed class PauseScreen
    {
        static readonly string[] Keys = { "ЛКМ", "ПКМ", "Q", "Space" };
        static readonly string[] KeysPad = { "X", "Y", "B", "A" };

        readonly Canvas canvas;
        readonly MenuList menu;
        readonly RectTransform summary, controls;
        readonly Text room, wave, kills, time;
        readonly Text[] slotKeys = new Text[4], slotNames = new Text[4];
        readonly Text[] trinketLines = new Text[TrinketBook.Slots];
        GameObject trinketCaption;
        readonly HintBar hints;

        public bool Visible => canvas.enabled;

        public PauseScreen(System.Action resume, System.Action skills, System.Action restart, System.Action toTitle)
        {
            canvas = UiKit.Canvas("Pause", 30);
            var t = canvas.transform;
            UiKit.Fill(t, "Dim", new Color(0.03f, 0.02f, 0.05f, 0.62f)).raycastTarget = true;
            var side = UiKit.Image(t, "Side", Pal.Panel, new Vector2(0f, 0.5f), Vector2.zero, new Vector2(640f, 2000f), new Vector2(0f, 0.5f));
            UiKit.Image(side.transform, "Edge", Pal.GoldDim, new Vector2(1f, 0.5f), Vector2.zero, new Vector2(2f, 2000f), new Vector2(1f, 0.5f));
            var title = UiKit.Label(t, "Пауза", 72, Pal.Gold, new Vector2(0f, 1f), new Vector2(84f, -96f), new Vector2(500f, 90f), TextAnchor.MiddleLeft, FontStyle.Normal);
            title.rectTransform.pivot = new Vector2(0f, 0.5f);
            UiKit.Image(t, "Rule", Pal.GoldDim, new Vector2(0f, 1f), new Vector2(86f, -150f), new Vector2(320f, 2f), new Vector2(0f, 0.5f));

            menu = new MenuList(t, new Vector2(64f, -200f), 480f);
            menu.Add("Продолжить", resume);
            menu.Add("Скиллы", skills);
            menu.Add("Управление", () => ShowControls(!controls.gameObject.activeSelf));
            menu.Add("Заново", restart);
            menu.Add("Выйти в меню", toTitle, danger: true);

            // сводка забега
            summary = UiKit.Rect(t, "Summary", new Vector2(0f, 1f), new Vector2(720f, -80f), new Vector2(1120f, 800f), new Vector2(0f, 1f));
            Caption(summary, "Текущий забег", -10f);
            room = Stat(summary, 0, "Комната", 460f);
            wave = Stat(summary, 1, "Волна", 200f);
            kills = Stat(summary, 2, "Убито", 200f);
            time = Stat(summary, 3, "Время", 200f);
            Caption(summary, "Скиллы", -190f);
            for (int i = 0; i < 4; i++)
            {
                float y = -250f - i * 62f;
                if (i < 3) UiKit.Image(summary, "Line", Pal.Line, new Vector2(0f, 1f), new Vector2(0f, y - 31f), new Vector2(1120f, 2f), new Vector2(0f, 0.5f));
                slotKeys[i] = UiKit.Label(summary, "", 24, Pal.Gold, new Vector2(0f, 1f), new Vector2(0f, y), new Vector2(130f, 50f), TextAnchor.MiddleLeft, FontStyle.Normal);
                slotKeys[i].rectTransform.pivot = new Vector2(0f, 0.5f);
                slotNames[i] = UiKit.Label(summary, "", 24, Pal.Text, new Vector2(0f, 1f), new Vector2(140f, y), new Vector2(980f, 50f), TextAnchor.MiddleLeft, FontStyle.Normal);
                slotNames[i].rectTransform.pivot = new Vector2(0f, 0.5f);
                slotNames[i].supportRichText = true;
            }

            var tc = UiKit.Label(summary, "Тринкеты", 22, Pal.Muted, new Vector2(0f, 1f), new Vector2(0f, -500f), new Vector2(600f, 30f), TextAnchor.MiddleLeft, FontStyle.Normal);
            tc.rectTransform.pivot = new Vector2(0f, 1f);
            trinketCaption = tc.gameObject;
            for (int i = 0; i < trinketLines.Length; i++)
            {
                trinketLines[i] = UiKit.Label(summary, "", 22, Pal.Text, new Vector2(0f, 1f), new Vector2(0f, -556f - i * 50f), new Vector2(1120f, 44f), TextAnchor.MiddleLeft, FontStyle.Normal);
                trinketLines[i].rectTransform.pivot = new Vector2(0f, 0.5f);
                trinketLines[i].supportRichText = true;
            }

            controls = ControlsPanel.Build(t, new Vector2(0f, 1f), new Vector2(720f, -80f), new Vector2(1100f, 560f));
            hints = new HintBar(t, new Vector2(0f, 0f), new Vector2(720f, 48f));
            canvas.enabled = false;
        }

        static void Caption(Transform root, string text, float y)
        {
            var c = UiKit.Label(root, text, 22, Pal.Muted, new Vector2(0f, 1f), new Vector2(0f, y), new Vector2(600f, 30f), TextAnchor.MiddleLeft, FontStyle.Normal);
            c.rectTransform.pivot = new Vector2(0f, 1f);
        }

        /// <summary>Плашка сводки: подпись сверху, значение снизу. Плашки идут слева направо.</summary>
        static Text Stat(Transform root, int index, string label, float width)
        {
            float x = 0f;
            float[] widths = { 460f, 200f, 200f, 200f };
            for (int i = 0; i < index; i++) x += widths[i] + 20f;
            var box = UiKit.Image(root, "Stat", Pal.Line, new Vector2(0f, 1f), new Vector2(x, -50f), new Vector2(width, 104f), new Vector2(0f, 1f));
            UiKit.Image(box.transform, "Bg", Pal.Card, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(width - 4f, 100f));
            var l = UiKit.Label(box.transform, label, 20, Pal.Faint, new Vector2(0f, 1f), new Vector2(18f, -26f), new Vector2(width - 30f, 30f), TextAnchor.MiddleLeft, FontStyle.Normal);
            l.rectTransform.pivot = new Vector2(0f, 0.5f);
            var v = UiKit.Label(box.transform, "", 32, Pal.Text, new Vector2(0f, 1f), new Vector2(18f, -68f), new Vector2(width - 30f, 44f), TextAnchor.MiddleLeft, FontStyle.Normal);
            v.rectTransform.pivot = new Vector2(0f, 0.5f);
            v.horizontalOverflow = HorizontalWrapMode.Overflow;
            return v;
        }

        void ShowControls(bool on)
        {
            controls.gameObject.SetActive(on);
            summary.gameObject.SetActive(!on);
        }

        public void Show()
        {
            if (canvas.enabled) return;
            canvas.enabled = true;
            menu.Focus = 0;
            ShowControls(false);
        }

        public void Hide() => canvas.enabled = false;

        /// <summary>Меню паузы: вверх/вниз, A / Enter. Открыто «Управление» — B закрывает его, а не паузу;
        /// возвращает true, если нажатие B уже обработано.</summary>
        public bool Navigate(InputReader input)
        {
            if (!canvas.enabled) return false;
            if (controls.gameObject.activeSelf && input.NavBack) { ShowControls(false); return true; }
            menu.Navigate(input);
            return false;
        }

        public void Sync(ArenaSim sim, float runTime, InputReader input)
        {
            if (!canvas.enabled) return;
            menu.Sync();

            room.text = sim.LocationTitle;
            wave.text = sim.Wave.ToString();
            kills.text = sim.Kills.ToString();
            int sec = Mathf.FloorToInt(runTime);
            time.text = $"{sec / 60:00}:{sec % 60:00}";
            var keys = input.UseMouse ? Keys : KeysPad;
            for (int i = 0; i < 4; i++)
            {
                slotKeys[i].text = keys[i];
                slotNames[i].text = SlotLine(sim, i);
            }
            trinketCaption.SetActive(sim.FormsMode);
            for (int i = 0; i < trinketLines.Length; i++)
            {
                trinketLines[i].gameObject.SetActive(sim.FormsMode);
                var t = sim.TrinketSlots[i];
                trinketLines[i].text = t == null
                    ? "<color=#6f6688>пусто</color>"
                    : $"{TrinketBook.Name(t.Kind)} <color=#{ColorUtility.ToHtmlStringRGB(Art.TrinketColor(t.Kind))}>{TrinketBook.Roman(t.Level)}</color>" +
                      $"  <color=#{ColorUtility.ToHtmlStringRGB(Pal.TextDim)}>— {TrinketBook.Bonus(t.Kind, t.Level, sim.TrinketCfg)}</color>";
            }
            if (input.UseMouse) hints.Set("ЛКМ", "или Enter — выбрать", "!Esc", "— продолжить", "!R", "— заново");
            else hints.Set("A", "выбрать", "!Menu / B", "продолжить", "!Y", "заново");
        }

        static string SlotLine(ArenaSim sim, int i)
        {
            if (!sim.FormsMode) return sim.Slots[i].Main.HasValue ? CardCatalog.Get(sim.Slots[i].Main.Value).Name : "<color=#6f6688>пусто</color>";
            var card = sim.Forms[i].Card;
            if (card == null) return "<color=#6f6688>пусто</color>";
            var sb = new System.Text.StringBuilder(FormText.Name(card.Form));
            var traits = card.Traits;
            for (int k = 0; k < traits.Count; k++)
            {
                var tr = traits[k];
                int lvl = card.Level(tr);
                sb.Append(k == 0 ? " · " : " + ");
                sb.Append($"<color=#{ColorUtility.ToHtmlStringRGB(Art.TraitColor(tr))}>{FormText.Name(tr)}{(lvl > 1 ? $" ×{lvl}" : "")}</color>");
            }
            // вливания: занятые ячейки — ярко, свободные — тускло, цветом редкости
            var rar = Art.RarityColor(card.Rarity);
            sb.Append($"  <color=#{ColorUtility.ToHtmlStringRGB(rar)}>{new string('◆', card.Souls.Count)}</color>");
            sb.Append($"<color=#{ColorUtility.ToHtmlStringRGBA(rar.WithAlpha(0.3f))}>{new string('◆', card.SoulCells - card.Souls.Count)}</color>");
            return sb.ToString();
        }
    }
}
