using Arena.View;
using UnityEngine;

namespace Arena.UI
{
    /// <summary>Стартовый экран в духе Hades: за затемнением — комната, слева название и меню
    /// (новый забег, управление, выход). Управление открывается панелью справа, B / Esc / Backspace — закрыть.</summary>
    public sealed class StartScreen
    {
        readonly Canvas canvas;
        readonly MenuList menu;
        readonly RectTransform controls;
        readonly HintBar hints;

        public bool Visible => canvas.enabled;

        public StartScreen(System.Action newRun, System.Action quit)
        {
            canvas = UiKit.Canvas("Start", 40);
            var t = canvas.transform;
            UiKit.Fill(t, "Dim", new Color(0.03f, 0.02f, 0.05f, 0.45f)).raycastTarget = true;
            var side = UiKit.Image(t, "Side", new Color(0.055f, 0.04f, 0.085f, 0.88f), new Vector2(0f, 0.5f), Vector2.zero, new Vector2(760f, 2000f), new Vector2(0f, 0.5f));
            UiKit.Image(side.transform, "Edge", Pal.GoldDim, new Vector2(1f, 0.5f), Vector2.zero, new Vector2(2f, 2000f), new Vector2(1f, 0.5f));

            var title = UiKit.Label(t, "ACE ARENA", 110, Pal.Gold, new Vector2(0f, 1f), new Vector2(100f, -150f), new Vector2(900f, 130f), TextAnchor.MiddleLeft, FontStyle.Normal);
            title.rectTransform.pivot = new Vector2(0f, 0.5f);
            var sub = UiKit.Label(t, "Смерть идёт за бессмертными", 28, Pal.Muted, new Vector2(0f, 1f), new Vector2(104f, -228f), new Vector2(700f, 40f), TextAnchor.MiddleLeft, FontStyle.Normal);
            sub.rectTransform.pivot = new Vector2(0f, 0.5f);
            UiKit.Image(t, "Rule", Pal.GoldDim, new Vector2(0f, 1f), new Vector2(104f, -268f), new Vector2(460f, 2f), new Vector2(0f, 0.5f));

            menu = new MenuList(t, new Vector2(80f, -330f), 520f);
            menu.Add("Новый забег", newRun);
            menu.Add("Управление", () => controls.gameObject.SetActive(!controls.gameObject.activeSelf));
            menu.Add("Выход", quit);

            controls = ControlsPanel.Build(t, new Vector2(1f, 0.5f), new Vector2(-110f, 40f), new Vector2(980f, 560f));
            controls.gameObject.SetActive(false);

            hints = new HintBar(t, new Vector2(0f, 0f), new Vector2(104f, 48f));
            var ver = UiKit.Label(t, $"v{Application.version} · прототип", 20, Pal.Faint, new Vector2(1f, 0f), new Vector2(-40f, 48f), new Vector2(500f, 30f), TextAnchor.MiddleRight, FontStyle.Normal);
            ver.rectTransform.pivot = new Vector2(1f, 0.5f);
            canvas.enabled = false;
        }

        public void Show()
        {
            canvas.enabled = true;
            menu.Focus = 0;
            controls.gameObject.SetActive(false);
        }

        public void Hide() => canvas.enabled = false;

        public void Sync(InputReader input)
        {
            if (!canvas.enabled) return;
            bool back = input.NavBack || input.PausePressed;
            if (controls.gameObject.activeSelf && back) controls.gameObject.SetActive(false);
            else menu.Navigate(input);
            menu.Sync();
            bool sub = controls.gameObject.activeSelf;
            if (input.UseMouse) hints.Set("ЛКМ", "или Enter — выбрать", sub ? "!Esc" : "", sub ? "— назад" : "");
            else hints.Set("A", "выбрать", sub ? "!B" : "", sub ? "назад" : "");
        }
    }
}
