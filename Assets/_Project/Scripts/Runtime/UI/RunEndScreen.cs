using Arena.Sim;
using Arena.View;
using UnityEngine;
using UnityEngine.UI;

namespace Arena.UI
{
    /// <summary>Итоги забега, как в Enter the Gungeon: «Забег пройден» или «Смерть повержена», до какого этажа дошла,
    /// сколько боёв пройдено, сколько убито, время. Один пункт — «В хаб» (A / Enter / клик).</summary>
    public sealed class RunEndScreen
    {
        readonly Canvas canvas;
        readonly Text title, stats;
        readonly MenuList menu;
        float openedAt;
        const float InputDelay = 0.6f; // удар или атака, зажатые в момент смерти, не должны сразу нажать кнопку

        public bool Visible => canvas.enabled;

        public RunEndScreen(System.Action toHub)
        {
            canvas = UiKit.Canvas("RunEnd", 30);
            var t = canvas.transform;
            UiKit.Fill(t, "Dim", new Color(0.03f, 0.02f, 0.05f, 0.78f)).raycastTarget = true;
            var box = UiKit.Image(t, "Box", Pal.GoldDim, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(820f, 560f));
            UiKit.Image(box.transform, "Bg", Pal.Panel, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(816f, 556f));
            title = UiKit.Label(box.transform, "", 60, Pal.Gold, new Vector2(0.5f, 1f), new Vector2(0f, -80f), new Vector2(760f, 80f));
            title.rectTransform.pivot = new Vector2(0.5f, 0.5f);
            stats = UiKit.Label(box.transform, "", 28, Pal.Text, new Vector2(0.5f, 1f), new Vector2(0f, -150f), new Vector2(700f, 220f), TextAnchor.UpperCenter, FontStyle.Normal);
            stats.rectTransform.pivot = new Vector2(0.5f, 1f);
            stats.lineSpacing = 1.3f;
            stats.supportRichText = true;
            menu = new MenuList(box.transform, new Vector2(230f, -420f), 360f);
            menu.Add("В хаб", toHub);
            canvas.enabled = false;
        }

        public void Show(ArenaSim sim, float runTime)
        {
            canvas.enabled = true;
            openedAt = Time.unscaledTime;
            menu.Focus = 0;
            title.text = sim.RunWon ? "Забег пройден" : "Смерть повержена";
            title.color = sim.RunWon ? Pal.Gold : Pal.Danger;
            int sec = Mathf.FloorToInt(runTime);
            string floor = sim.FloorMode ? $"{sim.FloorIndex + 1} из {ArenaSim.FloorOrder.Length}" : "—";
            stats.text = $"Этаж <color=#{Hex(Pal.Gold)}>{floor}</color>\n" +
                         $"Боёв пройдено <color=#{Hex(Pal.Gold)}>{sim.RunRoomsCleared}</color>\n" +
                         $"Убито <color=#{Hex(Pal.Gold)}>{sim.Kills}</color>\n" +
                         $"Время <color=#{Hex(Pal.Gold)}>{sec / 60:00}:{sec % 60:00}</color>";
        }

        public void Hide() => canvas.enabled = false;

        public void Sync(InputReader input)
        {
            if (!canvas.enabled) return;
            if (Time.unscaledTime - openedAt >= InputDelay) menu.Navigate(input);
            menu.Sync();
        }

        static string Hex(Color c) => ColorUtility.ToHtmlStringRGB(c);
    }
}
