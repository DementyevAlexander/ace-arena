using System.Collections.Generic;
using Arena.Core;
using Arena.Sim;
using Arena.View;
using UnityEngine;
using UnityEngine.UI;

namespace Arena.UI
{
    /// <summary>Меню персонажа хаба: Наставник — три набора по две карты (взять один), Торговец — открыть формы, свойства и тринкеты
    /// за Сардис, Хранитель — коллекция и найденные связки, Летописец — летопись забегов. Слева список (прокрутка), справа —
    /// описание выбранного. ↑ ↓ — выбор, A / Enter / клик по выбранному — действие, B / Esc — закрыть.</summary>
    public sealed class HubMenuScreen
    {
        const int Shown = 14;
        const float RowH = 50f, ListW = 960f, InputDelay = 0.35f;

        struct Row
        {
            public string Left, Right, Detail;
            public bool Header, Can;
            public System.Action Act;
        }

        readonly Canvas canvas;
        readonly ArenaSim sim;
        readonly Text title, currency, detail, hint;
        readonly RowView[] rows = new RowView[Shown];
        readonly List<Row> data = new();
        int focus, scroll;
        float openedAt;

        public bool Visible => canvas.enabled;

        public HubMenuScreen(ArenaSim sim)
        {
            this.sim = sim;
            canvas = UiKit.Canvas("HubMenu", 25);
            UiKit.Fill(canvas.transform, "Dim", new Color(0.03f, 0.02f, 0.05f, 0.9f)).raycastTarget = true;
            var t = UiKit.Rect(canvas.transform, "Layout", new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1920f, 1080f));
            title = Lbl(t, "", 46, Pal.Gold, 120f, 90f, 1100f, 60f, FontStyle.Bold);
            currency = Lbl(t, "", 30, Art.Sardis, 1160f, 90f, 640f, 50f, FontStyle.Bold);
            currency.alignment = TextAnchor.MiddleRight;
            for (int i = 0; i < Shown; i++) rows[i] = new RowView(this, t, i, 120f, 150f + i * (RowH + 4f));
            var box = UiKit.Image(t, "Detail", Pal.Line, new Vector2(0f, 1f), new Vector2(1110f, -150f), new Vector2(690f, Shown * (RowH + 4f) - 4f), new Vector2(0f, 1f));
            UiKit.Image(box.transform, "Bg", Pal.Panel, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(686f, Shown * (RowH + 4f) - 8f));
            detail = UiKit.Label(box.transform, "", 24, Pal.TextDim, new Vector2(0f, 1f), new Vector2(24f, -20f), new Vector2(640f, 680f), TextAnchor.UpperLeft, FontStyle.Normal);
            detail.rectTransform.pivot = new Vector2(0f, 1f);
            detail.supportRichText = true;
            detail.lineSpacing = 1.2f;
            hint = UiKit.Label(t, "", 22, Pal.Muted, new Vector2(0.5f, 1f), new Vector2(0f, -930f), new Vector2(1400f, 36f), TextAnchor.MiddleCenter, FontStyle.Normal);
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
            bool want = sim.MenuOpen;
            if (want != canvas.enabled)
            {
                canvas.enabled = want;
                openedAt = Time.unscaledTime;
                focus = -1; scroll = 0;
            }
            if (!want) return;
            Build();
            if (focus < 0 || focus >= data.Count || data[focus].Header) focus = NextSelectable(-1, 1);
            if (Time.unscaledTime - openedAt >= InputDelay)
            {
                if (input.NavY != 0) focus = NextSelectable(focus, input.NavY > 0 ? -1 : 1);
                if (input.NavConfirm) Act(focus);
                else if (input.NavBack || input.PausePressed) sim.CloseMenu();
            }
            if (!sim.MenuOpen) { canvas.enabled = false; return; }
            Draw(input.UseMouse);
        }

        int NextSelectable(int from, int dir)
        {
            for (int i = from + dir; i >= 0 && i < data.Count; i += dir) if (!data[i].Header) return i;
            return from >= 0 && from < data.Count ? from : -1;
        }

        void Act(int i)
        {
            if (i < 0 || i >= data.Count || !data[i].Can) return;
            data[i].Act?.Invoke();
        }

        void Click(int view)
        {
            int i = scroll + view;
            if (i >= data.Count || data[i].Header) return;
            if (i == focus) Act(i);
            else focus = i;
        }

        // ---------- содержимое ----------

        void Build()
        {
            data.Clear();
            var m = sim.Meta;
            switch (sim.Menu)
            {
                case HubRole.Mentor:
                    title.text = "Наставник — набор на забег";
                    currency.text = "";
                    data.Add(new Row { Header = true, Left = "Взять можно один · Снаряд в ЛКМ и Рывок в Space — всегда, набор — в ПКМ и Q" });
                    for (int i = 0; i < sim.MentorSets.Count; i++)
                    {
                        int k = i;
                        var set = sim.MentorSets[i];
                        data.Add(new Row
                        {
                            Left = $"Набор {i + 1}:  {CardName(set[0])}   +   {CardName(set[1])}",
                            Right = sim.ChosenSet == i ? $"<color=#{Hex(Pal.Gold)}>✓ взят</color>" : "",
                            Detail = SetDetail(set), Can = sim.ChosenSet != i, Act = () => sim.ChooseSet(k),
                        });
                    }
                    if (sim.MentorSets.Count == 0) data.Add(new Row { Left = "Коллекция пуста — нечего дать", Detail = "" });
                    break;
                case HubRole.Merchant:
                {
                    title.text = "Торговец — открыть за Сардис";
                    currency.text = $"◆ {m.sardis}  <size=22>Сардис</size>";
                    var locked = MetaShop.Locked(m);
                    UnlockKind? section = null;
                    foreach (var u in locked)
                    {
                        if (section != u.Kind)
                        {
                            section = u.Kind;
                            data.Add(new Row { Header = true, Left = u.Kind switch { UnlockKind.Form => "Формы", UnlockKind.Trait => "Свойства", _ => "Тринкеты" } + $"  —  ◆ {u.Price}" });
                        }
                        var item = u;
                        bool can = sim.CanBuy(u);
                        data.Add(new Row
                        {
                            Left = u.Kind == UnlockKind.Trait ? $"<color=#{Hex(Art.TraitColor((Trait)u.Id))}>{u.Name}</color>"
                                : u.Kind == UnlockKind.Trinket ? $"<color=#{Hex(Art.TrinketColor((TrinketKind)u.Id))}>{u.Name}</color>" : u.Name,
                            Right = $"<color=#{Hex(can ? Art.Sardis : Pal.Faint)}>◆ {u.Price}</color>",
                            Detail = UnlockDetail(u) + (can ? "" : $"\n\n<color=#{Hex(Pal.Danger)}>Не хватает Сардиса: нужно {u.Price}, есть {m.sardis}.</color>"),
                            Can = can, Act = () => sim.Buy(item),
                        });
                    }
                    if (locked.Count == 0) data.Add(new Row { Left = "Открыто всё", Detail = "" });
                    break;
                }
                case HubRole.Keeper:
                {
                    title.text = "Хранитель — коллекция";
                    currency.text = $"◆ {m.sardis}  <size=22>Сардис</size>";
                    var forms = (FormKind[])System.Enum.GetValues(typeof(FormKind));
                    data.Add(new Row { Header = true, Left = $"Формы  {m.forms.Count} / {forms.Length}" });
                    foreach (var f in forms) data.Add(Owned(m.Has(f), FormText.Name(f), FormText.Base(f)));
                    data.Add(new Row { Header = true, Left = $"Свойства  {m.traits.Count} / {Traits.Count}" });
                    for (int i = 0; i < Traits.Count; i++) data.Add(Owned(m.Has((Trait)i), FormText.Name((Trait)i), UnlockDetail(new Unlockable(UnlockKind.Trait, i))));
                    data.Add(new Row { Header = true, Left = $"Тринкеты  {m.trinkets.Count} / {TrinketBook.All.Length}" });
                    foreach (var k in TrinketBook.All) data.Add(Owned(m.Has(k), TrinketBook.Name(k), UnlockDetail(new Unlockable(UnlockKind.Trinket, (int)k))));
                    data.Add(new Row { Header = true, Left = $"Связки  {m.bonds.Count} / {Bonds.All.Length}" });
                    foreach (var b in Bonds.All)
                    {
                        bool seen = m.Has(b);
                        data.Add(Owned(seen, seen ? Bonds.Name(b) : "???", seen ? FormText.BondLine(b) : "Ещё не собиралась. Два свойства на одной карте дают третий эффект."));
                    }
                    data.Add(new Row { Header = true, Left = $"Карт можно получить: {m.CardCount}  (открытые формы × открытые свойства)" });
                    break;
                }
                default:
                {
                    title.text = "Летописец — летопись";
                    currency.text = $"◆ {m.sardis}  <size=22>Сардис</size>";
                    data.Add(new Row { Left = "Забегов", Right = m.runs.ToString(), Detail = "Сколько раз Серафим уходил во врата." });
                    data.Add(new Row { Left = "Побед", Right = m.wins.ToString(), Detail = "Выход с пятого этажа." });
                    data.Add(new Row { Left = "Лучший этаж", Right = m.bestFloor.ToString(), Detail = "" });
                    data.Add(new Row { Left = "Убито всего", Right = m.totalKills.ToString(), Detail = "" });
                    data.Add(new Row { Left = "Мегера повержена", Right = m.megaeraKills.ToString(), Detail = "Каждая победа над ней — Сардис." });
                    data.Add(new Row { Header = true, Left = "Последние забеги" });
                    foreach (var r in m.history)
                        data.Add(new Row
                        {
                            Left = r.won ? $"<color=#{Hex(Pal.Gold)}>Победа</color>" : $"Этаж {r.floor} — пал",
                            Right = $"убито {r.kills} · комнат {r.rooms} · {Mathf.FloorToInt(r.time / 60f)}:{Mathf.FloorToInt(r.time % 60f):00}",
                            Detail = "",
                        });
                    if (m.history.Count == 0) data.Add(new Row { Left = "Страница пуста", Detail = "" });
                    break;
                }
            }
        }

        static Row Owned(bool owned, string name, string detail) => new()
        {
            Left = owned ? name : $"<color=#{Hex(Pal.Faint)}>{name}</color>",
            Right = owned ? $"<color=#{Hex(Pal.Gold)}>✓</color>" : $"<color=#{Hex(Pal.Faint)}>закрыто</color>",
            Detail = detail,
        };

        static string CardName(SkillCard c) =>
            c.Own.HasValue ? $"{FormText.Name(c.Form)} · <color=#{Hex(Art.TraitColor(c.Own.Value))}>{FormText.Name(c.Own.Value)}</color>" : FormText.Name(c.Form);

        static string SetDetail(SkillCard[] set)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var c in set)
            {
                if (sb.Length > 0) sb.Append("\n\n");
                sb.Append($"<b>{CardName(c)}</b>\n");
                sb.Append(string.Join("\n", FormText.Describe(c)));
            }
            return sb.ToString();
        }

        static string UnlockDetail(Unlockable u)
        {
            switch (u.Kind)
            {
                case UnlockKind.Form: return FormText.Base((FormKind)u.Id);
                case UnlockKind.Trait:
                {
                    var t = (Trait)u.Id;
                    var sb = new System.Text.StringBuilder($"<b>{FormText.Name(t)}</b> на формах:");
                    foreach (FormKind f in System.Enum.GetValues(typeof(FormKind))) sb.Append($"\n{FormText.Name(f)}: {FormText.Cell(t, f)}");
                    return sb.ToString();
                }
                default:
                {
                    var k = (TrinketKind)u.Id;
                    var c = new TrinketTuning();
                    string extra = TrinketBook.Group(k) == TrinketGroup.Curse ? $"Плата: {TrinketBook.Curse(k, c)}" : $"III: {TrinketBook.Special(k)}";
                    return $"<b>{TrinketBook.Name(k)}</b> · {TrinketBook.Category(k)}\nI: {TrinketBook.Bonus(k, 1, c)}\n{extra}";
                }
            }
        }

        // ---------- вид ----------

        void Draw(bool mouse)
        {
            if (focus >= 0)
            {
                if (focus < scroll) scroll = focus;
                if (focus >= scroll + Shown) scroll = focus - Shown + 1;
            }
            scroll = Mathf.Clamp(scroll, 0, Mathf.Max(0, data.Count - Shown));
            for (int v = 0; v < Shown; v++)
            {
                int i = scroll + v;
                if (i < data.Count) rows[v].Sync(data[i], i == focus);
                else rows[v].Hide();
            }
            detail.text = focus >= 0 && focus < data.Count ? data[focus].Detail ?? "" : "";
            string verb = sim.Menu switch { HubRole.Mentor => "взять набор", HubRole.Merchant => "открыть", _ => "" };
            hint.text = verb.Length > 0
                ? (mouse ? $"↑ ↓ или клик — выбрать · Enter или клик по выбранному — {verb} · Esc — закрыть" : $"↑ ↓ — выбрать · A — {verb} · B — закрыть")
                : (mouse ? "↑ ↓ или клик — выбрать · Esc — закрыть" : "↑ ↓ — выбрать · B — закрыть");
        }

        sealed class RowView
        {
            readonly Image frame, bg;
            readonly Text left, right;

            public RowView(HubMenuScreen screen, Transform parent, int index, float x, float y)
            {
                frame = UiKit.Image(parent, "Row", Pal.Line, new Vector2(0f, 1f), new Vector2(x, -y), new Vector2(ListW, RowH), new Vector2(0f, 1f));
                frame.raycastTarget = true;
                var btn = frame.gameObject.AddComponent<Button>();
                btn.transition = Selectable.Transition.None;
                btn.onClick.AddListener(() => screen.Click(index));
                bg = UiKit.Image(frame.transform, "Bg", Pal.Card, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(ListW - 4f, RowH - 4f));
                left = UiKit.Label(frame.transform, "", 24, Pal.Text, new Vector2(0f, 0.5f), new Vector2(22f, 0f), new Vector2(ListW - 260f, RowH), TextAnchor.MiddleLeft, FontStyle.Normal);
                left.rectTransform.pivot = new Vector2(0f, 0.5f);
                left.supportRichText = true;
                right = UiKit.Label(frame.transform, "", 22, Pal.Muted, new Vector2(1f, 0.5f), new Vector2(-22f, 0f), new Vector2(240f, RowH), TextAnchor.MiddleRight, FontStyle.Bold);
                right.rectTransform.pivot = new Vector2(1f, 0.5f);
                right.supportRichText = true;
            }

            public void Sync(Row r, bool focused)
            {
                if (!frame.gameObject.activeSelf) frame.gameObject.SetActive(true);
                frame.color = r.Header ? new Color(0f, 0f, 0f, 0f) : focused ? Pal.Gold : Pal.Line;
                bg.color = r.Header ? new Color(0f, 0f, 0f, 0f) : focused ? Pal.Focus : Pal.Card;
                left.text = r.Header ? $"<color=#{Hex(Pal.Muted)}>{r.Left}</color>" : r.Left;
                left.rectTransform.sizeDelta = new Vector2(r.Header ? ListW - 44f : ListW - 260f, RowH); // заголовок — во всю строку
                left.fontStyle = r.Header ? FontStyle.Bold : FontStyle.Normal;
                right.text = r.Right ?? "";
            }

            public void Hide() { if (frame.gameObject.activeSelf) frame.gameObject.SetActive(false); }
        }
    }
}
