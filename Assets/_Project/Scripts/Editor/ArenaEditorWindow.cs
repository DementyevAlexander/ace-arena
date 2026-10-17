using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Arena.Core;
using Arena.Sim;
using UnityEditor;
using UnityEngine;

namespace Arena.EditorTools
{
    /// <summary>Arena → Arena Editor: простой редактор арен для этажей. Сетка 32×32 по клеткам 1 ю, пять слоёв (ArenaLayout);
    /// правка меняет только активный слой. Форма — штрих по первой клетке (была пустой — добавляет, отмеченной — стирает,
    /// каждая клетка — раз за штрих), новые клетки сразу получают пол. Поверхность — ЛКМ кладёт выбранный тип, ПКМ стирает.
    /// Объекты — ЛКМ ставит (держать — двигать с привязкой), ЛКМ по объекту — перенести, ПКМ — удалить, R / колесо — поворот.
    /// Ctrl+Z / Ctrl+Y — отмена и возврат. Справа — проверка (ArenaCheck): ошибки не пускают арену в генерацию.</summary>
    public sealed class ArenaEditorWindow : EditorWindow
    {
        const string Dir = "Assets/_Project/Resources/" + ArenaLibrary.Folder;
        const float PanelWidth = 300f;
        const int MaxUndo = 200;

        // переживают перекомпиляцию скриптов и вход в Play Mode
        [SerializeField] ArenaLayout arena;
        [SerializeField] string fileName;
        [SerializeField] bool dirty;
        [SerializeField] int layer;
        [SerializeField] int presetIndex;
        [SerializeField] int surfaceIndex;
        [SerializeField] int[] pieceIndex = new int[5];
        [SerializeField] int rot;
        ArenaCheck.Report report;
        int selectedIssue = -1;
        Vector2 panelScroll;
        ArenaLibrary.Index index;

        [SerializeField] List<string> undo = new(), redo = new();

        // текущий жест мышью
        enum Gesture { None, Shape, Surface, Place, Move }
        Gesture gesture;
        bool shapeAdd;
        readonly HashSet<Vector2Int> stroke = new();
        bool strokeChanged;
        string strokeBefore;
        ArenaObject ghost, moving;
        Vector2Int grab, moveFrom;
        int moveRotFrom;
        Vector2Int? hover;

        Rect gridRect;
        float cell;

        [MenuItem("Arena/Arena Editor")]
        static void Open() => GetWindow<ArenaEditorWindow>("Arena Editor").Show();

        /// <summary>Арены Тартара (этажи) — в арены редактора t_*: включены в генерацию и заменяют оригиналы (onlyEditor).
        /// Из командной строки: -executeMethod Arena.EditorTools.ArenaEditorWindow.ImportTartarus.</summary>
        [MenuItem("Arena/Import Tartarus Arenas to Editor")]
        public static void ImportTartarus()
        {
            var index = LoadIndexFile();
            var names = new List<string>(index.arenas);
            var enabled = new List<string>(index.enabled);
            Directory.CreateDirectory(Dir);
            var report = new List<string>();
            foreach (var room in RoomLibrary.All)
            {
                var d = RoomLibrary.Get(room);
                if (d == null || d.biome != ArenaSim.ArenaBiome) continue;
                var name = ArenaImport.Prefix + room;
                var a = ArenaImport.FromRoom(d, name);
                var path = $"{Dir}/{name}.json";
                File.WriteAllText(path, a.ToJson());
                AssetDatabase.ImportAsset(path);
                if (!names.Contains(name)) names.Add(name);
                if (!enabled.Contains(name)) enabled.Add(name);
                var rep = ArenaCheck.Check(a);
                report.Add($"{name}: {a.ShapeCount()} кл., объектов {a.objects.Count}, выходов {rep.WorkingExits}" + (rep.Admitted ? "" : " — ЕСТЬ ОШИБКИ"));
            }
            index.arenas = names.ToArray();
            index.enabled = enabled.ToArray();
            index.onlyEditor = true;
            File.WriteAllText(IndexPath, JsonUtility.ToJson(index, true));
            AssetDatabase.ImportAsset(IndexPath);
            ArenaSim.ReloadArenas();
            Debug.Log("Импорт арен Тартара:\n" + string.Join("\n", report));
        }

        void OnEnable()
        {
            wantsMouseMove = true;
            minSize = new Vector2(760f, 520f);
            index = LoadIndexFile();
            if (pieceIndex == null || pieceIndex.Length != 5) pieceIndex = new int[5];
            if (arena == null || arena.ShapeCount() == 0 && string.IsNullOrEmpty(arena.name)) NewArena();
            else Changed(false);
        }

        // ---------- файл ----------

        static string IndexPath => Dir + "/index.json";

        static ArenaLibrary.Index LoadIndexFile() =>
            File.Exists(IndexPath) ? JsonUtility.FromJson<ArenaLibrary.Index>(File.ReadAllText(IndexPath)) ?? new ArenaLibrary.Index() : new ArenaLibrary.Index();

        void SaveIndexFile()
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(IndexPath, JsonUtility.ToJson(index, true));
            AssetDatabase.ImportAsset(IndexPath);
        }

        bool Enabled(string name) => System.Array.IndexOf(index.enabled, name) >= 0;

        void SetEnabled(string name, bool on)
        {
            var list = new List<string>(index.enabled);
            list.Remove(name);
            if (on) list.Add(name);
            index.enabled = list.ToArray();
        }

        void NewArena()
        {
            int n = 1;
            while (File.Exists($"{Dir}/arena_{n:00}.json")) n++;
            arena = new ArenaLayout($"arena_{n:00}");
            fileName = null;
            dirty = false;
            undo.Clear(); redo.Clear();
            Changed(false);
        }

        bool ConfirmDiscard() => !dirty || EditorUtility.DisplayDialog("Arena Editor", $"Арена «{arena.name}» не сохранена. Продолжить без сохранения?", "Да", "Отмена");

        void OpenMenu()
        {
            var menu = new GenericMenu();
            var names = new List<string>();
            if (Directory.Exists(Dir))
                foreach (var f in Directory.GetFiles(Dir, "*.json"))
                {
                    var n = Path.GetFileNameWithoutExtension(f);
                    if (n != "index") names.Add(n);
                }
            names.Sort();
            if (names.Count == 0) menu.AddDisabledItem(new GUIContent("Нет сохранённых арен"));
            foreach (var n in names)
            {
                var name = n;
                menu.AddItem(new GUIContent(name), name == fileName, () => Load(name));
            }
            menu.ShowAsContext();
        }

        void Load(string name)
        {
            if (!ConfirmDiscard()) return;
            var path = $"{Dir}/{name}.json";
            if (!File.Exists(path)) return;
            arena = ArenaLayout.FromJson(File.ReadAllText(path));
            arena.name = name;
            fileName = name;
            dirty = false;
            undo.Clear(); redo.Clear();
            Changed(false);
        }

        void Save()
        {
            var name = arena.name?.Trim() ?? "";
            if (!Regex.IsMatch(name, "^[A-Za-z0-9_-]+$") || name == "index")
            {
                EditorUtility.DisplayDialog("Arena Editor", "Имя арены — латиница, цифры, «_» и «-» (не «index»).", "OK");
                return;
            }
            var path = $"{Dir}/{name}.json";
            if (name != fileName && File.Exists(path) && !EditorUtility.DisplayDialog("Arena Editor", $"Арена «{name}» уже есть. Перезаписать?", "Перезаписать", "Отмена")) return;
            Directory.CreateDirectory(Dir);
            arena.name = name;
            File.WriteAllText(path, arena.ToJson());
            AssetDatabase.ImportAsset(path);
            bool wasNew = fileName != name && System.Array.IndexOf(index.arenas, name) < 0;
            if (System.Array.IndexOf(index.arenas, name) < 0) index.arenas = new List<string>(index.arenas) { name }.ToArray();
            if (wasNew) SetEnabled(name, true); // новая арена — сразу в генерацию (если пройдёт проверку)
            SaveIndexFile();
            fileName = name;
            dirty = false;
            ArenaSim.ReloadArenas();
            ShowNotification(new GUIContent(report.Admitted ? $"Сохранено: {name}" : $"Сохранено: {name} (есть ошибки — в генерацию не попадёт)"));
        }

        // ---------- отмена ----------

        void Snapshot(string before)
        {
            undo.Add(before);
            if (undo.Count > MaxUndo) undo.RemoveAt(0);
            redo.Clear();
        }

        void Undo()
        {
            if (undo.Count == 0) return;
            redo.Add(arena.ToJson());
            Restore(undo[^1]);
            undo.RemoveAt(undo.Count - 1);
        }

        void Redo()
        {
            if (redo.Count == 0) return;
            undo.Add(arena.ToJson());
            Restore(redo[^1]);
            redo.RemoveAt(redo.Count - 1);
        }

        void Restore(string json)
        {
            var name = arena.name;
            arena = ArenaLayout.FromJson(json);
            arena.name = name;
            Changed(true);
        }

        void Changed(bool markDirty = true)
        {
            if (markDirty) dirty = true;
            report = ArenaCheck.Check(arena);
            if (selectedIssue >= report.Issues.Count) selectedIssue = -1;
            Repaint();
        }

        /// <summary>Действие одним шагом отмены.</summary>
        void Act(System.Action change)
        {
            var before = arena.ToJson();
            change();
            if (arena.ToJson() == before) return;
            Snapshot(before);
            Changed();
        }

        // ---------- окно ----------

        void OnGUI()
        {
            if (arena == null) NewArena();
            if (report == null) Changed(false);
            Keys();
            EditorGUILayout.BeginHorizontal();
            Panel();
            Grid();
            EditorGUILayout.EndHorizontal();
        }

        void Keys()
        {
            var e = Event.current;
            if (e.type != EventType.KeyDown || GUIUtility.keyboardControl != 0) return; // печатают в поле имени
            bool mod = e.control || e.command;
            if (mod && e.keyCode == KeyCode.Z && !e.shift) { Undo(); e.Use(); }
            else if (mod && (e.keyCode == KeyCode.Y || e.keyCode == KeyCode.Z && e.shift)) { Redo(); e.Use(); }
            else if (!mod && e.keyCode == KeyCode.R && layer >= ArenaLayout.Traps) { Rotate(e.shift ? -1 : 1); e.Use(); }
            else if (!mod && e.keyCode >= KeyCode.Alpha0 && e.keyCode <= KeyCode.Alpha4) { SetLayer(e.keyCode - KeyCode.Alpha0); e.Use(); }
        }

        void SetLayer(int l)
        {
            layer = l;
            CancelGesture();
            Repaint();
        }

        void Rotate(int d)
        {
            rot = (rot + d + 4) & 3;
            if (ghost != null) ghost.rot = rot;
            if (moving != null) moving.rot = (moving.rot + d + 4) & 3;
            Repaint();
        }

        void Panel()
        {
            EditorGUILayout.BeginVertical(GUILayout.Width(PanelWidth));
            panelScroll = EditorGUILayout.BeginScrollView(panelScroll);

            EditorGUILayout.LabelField("Арена", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Новая") && ConfirmDiscard()) NewArena();
            if (GUILayout.Button("Открыть…")) OpenMenu();
            if (GUILayout.Button(dirty ? "Сохранить*" : "Сохранить")) Save();
            EditorGUILayout.EndHorizontal();
            var nm = EditorGUILayout.TextField("Имя", arena.name);
            if (nm != arena.name) { arena.name = nm; dirty = true; }

            int roofIdx = arena.roof == ArenaLayout.RoofOpen ? 1 : arena.roof == ArenaLayout.RoofClosed ? 2 : 0;
            int nr = EditorGUILayout.Popup("Тип", roofIdx, new[] { "Авто (треть — залы)", "Открытая", "Закрытая (стены и крыша)" });
            if (nr != roofIdx) Act(() => arena.roof = nr == 1 ? ArenaLayout.RoofOpen : nr == 2 ? ArenaLayout.RoofClosed : ArenaLayout.RoofAuto);

            if (fileName != null)
            {
                bool on = Enabled(fileName);
                bool non = EditorGUILayout.ToggleLeft("В генерацию этажа", on);
                if (non != on) { SetEnabled(fileName, non); SaveIndexFile(); ArenaSim.ReloadArenas(); }
            }
            bool only = EditorGUILayout.ToggleLeft("Этажи только из арен редактора (проверка)", index.onlyEditor);
            if (only != index.onlyEditor) { index.onlyEditor = only; SaveIndexFile(); ArenaSim.ReloadArenas(); }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Слой", EditorStyles.boldLabel);
            int nl = GUILayout.SelectionGrid(layer, ArenaLayout.LayerNames, 1);
            if (nl != layer) SetLayer(nl);

            EditorGUILayout.Space();
            LayerContent();

            EditorGUILayout.Space();
            Checks();

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "В игре сетка повёрнута на 45°, как комнаты Hades: вправо (→) — на экране вправо-вверх, вверх (↑) — влево-вверх.\n" +
                "1 клетка = 1 ю. Цифры 0–4 — слой. Ctrl+Z / Ctrl+Y — отмена и возврат.", MessageType.None);

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        void LayerContent()
        {
            switch (layer)
            {
                case ArenaLayout.Shape:
                {
                    var names = System.Array.ConvertAll(ArenaPresets.All, p => $"{p.Title} · {ArenaPresets.Area(p)} кл.");
                    presetIndex = EditorGUILayout.Popup("Пресет", presetIndex, names);
                    EditorGUILayout.BeginHorizontal();
                    if (GUILayout.Button("Применить пресет")) Act(() => ArenaPresets.Apply(arena, ArenaPresets.All[presetIndex]));
                    if (GUILayout.Button("Очистить форму")) Act(() => { for (int y = 0; y < arena.h; y++) for (int x = 0; x < arena.w; x++) arena.SetIn(x, y, false); });
                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.HelpBox("ЛКМ — клетка; штрих добавляет, если начат на пустой клетке, и стирает, если на отмеченной. " +
                                            "Новые клетки получают пол. Поверхность и объекты при этом не стираются.", MessageType.None);
                    EditorGUILayout.LabelField($"Клеток формы: {arena.ShapeCount()}");
                    break;
                }
                case ArenaLayout.Surface:
                {
                    var names = System.Array.ConvertAll(ArenaSurface.All, s => s.Name);
                    surfaceIndex = EditorGUILayout.Popup("Поверхность", surfaceIndex, names);
                    if (GUILayout.Button($"Залить пустые клетки формы: {ArenaSurface.All[surfaceIndex].Name.ToLowerInvariant()}"))
                        Act(() =>
                        {
                            for (int y = 0; y < arena.h; y++)
                                for (int x = 0; x < arena.w; x++)
                                    if (arena.In(x, y) && arena.Surf(x, y) == ArenaLayout.NoSurface) arena.SetSurf(x, y, ArenaSurface.All[surfaceIndex].Code);
                        });
                    EditorGUILayout.HelpBox("ЛКМ — положить, ПКМ — стереть (нет поверхности). Только внутри формы.", MessageType.None);
                    break;
                }
                default:
                {
                    var pieces = ArenaPieces.OfLayer(layer);
                    var names = pieces.ConvertAll(p => p.W == 1 && p.H == 1 || p.Name.Contains("×") ? p.Name : $"{p.Name} {p.W}×{p.H}").ToArray();
                    pieceIndex[layer] = Mathf.Clamp(EditorGUILayout.Popup("Объект", pieceIndex[layer], names), 0, pieces.Count - 1);
                    var pc = pieces[pieceIndex[layer]];
                    EditorGUILayout.LabelField("Поворот", $"{rot * 90}°{(pc.Directional ? $" (бьёт {Arrow(rot)})" : "")}   R / колесо");
                    EditorGUILayout.HelpBox("ЛКМ — поставить (держать — двигать по клеткам, отпустить — поставить). ЛКМ по объекту — перенести. " +
                                            "ПКМ — удалить. Ставить можно только на свободный пол внутри формы.", MessageType.None);
                    break;
                }
            }
        }

        static string Arrow(int r) => (r & 3) switch { 0 => "→", 1 => "↑", 2 => "←", _ => "↓" };

        void Checks()
        {
            int errors = 0;
            foreach (var i in report.Issues) if (i.Error) errors++;
            EditorGUILayout.LabelField("Проверка", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(report.Admitted ? "✓ Арена допущена к генерации" : $"✗ Ошибок: {errors} — в генерацию не попадёт", EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"Выходов: {report.WorkingExits} рабочих из {report.Exits.Count} · клеток для врагов: {report.Spawns.Count}");
            for (int k = 0; k < report.Issues.Count; k++)
            {
                var i = report.Issues[k];
                var style = new GUIStyle(EditorStyles.helpBox) { wordWrap = true, fontSize = 11 };
                style.normal.textColor = i.Error ? new Color(1f, 0.45f, 0.4f) : new Color(0.95f, 0.8f, 0.35f);
                var label = (i.Error ? "Ошибка: " : "Предупреждение: ") + i.Text + (i.Cells.Count > 0 ? (k == selectedIssue ? "  [показано]" : "  [показать]") : "");
                if (GUILayout.Button(label, style)) { selectedIssue = k == selectedIssue ? -1 : k; Repaint(); }
            }
            if (HasHiddenData() && GUILayout.Button("Удалить данные за пределами формы…")
                && EditorUtility.DisplayDialog("Arena Editor", "Удалить поверхность и объекты за пределами формы? Это можно отменить (Ctrl+Z).", "Удалить", "Отмена"))
                Act(RemoveHiddenData);
        }

        bool HasHiddenData()
        {
            for (int y = 0; y < arena.h; y++) for (int x = 0; x < arena.w; x++) if (!arena.In(x, y) && arena.Surf(x, y) != ArenaLayout.NoSurface) return true;
            foreach (var o in arena.objects) if (OutsideShape(o)) return true;
            return false;
        }

        bool OutsideShape(ArenaObject o)
        {
            var r = ArenaLayout.Footprint(o);
            for (int y = r.yMin; y < r.yMax; y++) for (int x = r.xMin; x < r.xMax; x++) if (!arena.In(x, y)) return true;
            return false;
        }

        void RemoveHiddenData()
        {
            for (int y = 0; y < arena.h; y++) for (int x = 0; x < arena.w; x++) if (!arena.In(x, y)) arena.SetSurf(x, y, ArenaLayout.NoSurface);
            arena.objects.RemoveAll(OutsideShape);
        }

        // ---------- сетка ----------

        void Grid()
        {
            var area = GUILayoutUtility.GetRect(10f, 10000f, 10f, 10000f, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
            cell = Mathf.Floor(Mathf.Min((area.width - 16f) / arena.w, (area.height - 16f) / arena.h));
            cell = Mathf.Max(cell, 6f);
            gridRect = new Rect(area.x + (area.width - cell * arena.w) * 0.5f, area.y + (area.height - cell * arena.h) * 0.5f, cell * arena.w, cell * arena.h);
            Mouse();
            if (Event.current.type == EventType.Repaint) Draw(area);
        }

        Rect CellRect(int x, int y) => new(gridRect.x + x * cell, gridRect.yMax - (y + 1) * cell, cell, cell);
        Rect AreaRect(RectInt r) => new(gridRect.x + r.xMin * cell, gridRect.yMax - r.yMax * cell, r.width * cell, r.height * cell);

        Vector2Int? CellAt(Vector2 m)
        {
            if (!gridRect.Contains(m)) return null;
            int x = Mathf.FloorToInt((m.x - gridRect.x) / cell), y = Mathf.FloorToInt((gridRect.yMax - m.y) / cell);
            return arena.Inside(x, y) ? new Vector2Int(x, y) : null;
        }

        static readonly Color Bg = new(0.13f, 0.13f, 0.14f), ShapeCol = new(0.42f, 0.42f, 0.44f), Line = new(0f, 0f, 0f, 0.25f),
            TrapCol = new(0.85f, 0.18f, 0.16f), LowCol = new(0.45f, 0.7f, 0.4f), HighCol = new(0.85f, 0.85f, 0.95f),
            ExitOk = new(0.3f, 0.95f, 0.45f), ExitBad = new(1f, 0.3f, 0.3f), Mark = new(1f, 0.2f, 1f, 0.9f);

        void Draw(Rect area)
        {
            EditorGUI.DrawRect(area, Bg);
            bool shapeActive = layer == ArenaLayout.Shape, surfActive = layer == ArenaLayout.Surface;
            for (int y = 0; y < arena.h; y++)
                for (int x = 0; x < arena.w; x++)
                {
                    var r = CellRect(x, y);
                    bool inside = arena.In(x, y);
                    var s = ArenaSurface.Get(arena.Surf(x, y));
                    if (inside)
                    {
                        // форма — серая, как силуэт комнат на мини-карте; поверхность поверх (на слое формы — приглушённо)
                        var c = ShapeCol;
                        if (s != null) c = Color.Lerp(ShapeCol, s.Color, shapeActive ? 0.35f : 1f);
                        EditorGUI.DrawRect(r, c);
                        if (s == null) Hatch(r, new Color(1f, 1f, 1f, 0.25f));
                    }
                    else if (s != null)
                    {
                        // скрытые данные за формой
                        EditorGUI.DrawRect(r, new Color(s.Color.r, s.Color.g, s.Color.b, surfActive ? 0.35f : 0.18f));
                        Hatch(r, new Color(1f, 0.4f, 0.4f, 0.6f));
                    }
                }
            // линии сетки
            for (int x = 0; x <= arena.w; x++) EditorGUI.DrawRect(new Rect(gridRect.x + x * cell, gridRect.y, 1f, gridRect.height), x % 4 == 0 ? new Color(0f, 0f, 0f, 0.45f) : Line);
            for (int y = 0; y <= arena.h; y++) EditorGUI.DrawRect(new Rect(gridRect.x, gridRect.yMax - y * cell, gridRect.width, 1f), y % 4 == 0 ? new Color(0f, 0f, 0f, 0.45f) : Line);

            // объекты: активный слой ярко, остальные приглушены
            foreach (var o in arena.objects)
            {
                if (o == moving) continue;
                DrawObject(o, layer == ArenaPieces.Get(o.id)?.Layer ? 1f : 0.4f, null);
            }
            if (moving != null) DrawObject(moving, 1f, CanPlace(moving, moving) ? ExitOk : ExitBad);
            if (ghost != null) DrawObject(ghost, 0.85f, CanPlace(ghost, null) ? ExitOk : ExitBad);

            // выходы — полоски у кромки снаружи
            foreach (var e in report.Exits)
            {
                var v = EtgData.DirVec(e.Dir);
                foreach (var l in e.Lanes)
                {
                    var r = CellRect(l.x + v.x, l.y + v.y);
                    float t = cell * 0.3f;
                    var bar = v.x != 0 ? new Rect(v.x > 0 ? r.x : r.xMax - t, r.y, t, r.height) : new Rect(r.x, v.y > 0 ? r.yMax - t : r.y, r.width, t);
                    EditorGUI.DrawRect(bar, e.Working ? ExitOk : ExitBad);
                }
            }

            // ячейки выбранной ошибки
            if (selectedIssue >= 0 && selectedIssue < report.Issues.Count)
                foreach (var c in report.Issues[selectedIssue].Cells) Outline(CellRect(c.x, c.y), Mark, 2f);

            if (hover is Vector2Int h && ghost == null && moving == null) Outline(CellRect(h.x, h.y), new Color(1f, 1f, 1f, 0.7f), 1f);
            if (hover is Vector2Int hh) GUI.Label(new Rect(gridRect.x, gridRect.yMax + 1f, 200f, 16f), $"({hh.x}, {hh.y})", EditorStyles.miniLabel);
        }

        void DrawObject(ArenaObject o, float alpha, Color? outline)
        {
            var p = ArenaPieces.Get(o.id);
            var area = AreaRect(ArenaLayout.Footprint(o));
            var col = p == null ? Color.magenta : p.Layer == ArenaLayout.Traps ? TrapCol : p.Layer == ArenaLayout.Low ? LowCol : HighCol;
            col.a = alpha;
            var inner = new Rect(area.x + 2f, area.y + 2f, area.width - 4f, area.height - 4f);
            EditorGUI.DrawRect(inner, col);
            if (p != null && cell >= 12f)
            {
                var style = new GUIStyle(EditorStyles.miniBoldLabel) { alignment = TextAnchor.MiddleCenter };
                style.normal.textColor = new Color(0f, 0f, 0f, alpha);
                string txt = p.Directional ? Arrow(o.rot) : p.Name.Substring(0, 1);
                GUI.Label(inner, txt, style);
            }
            if (outline is Color oc) Outline(area, oc, 2f);
        }

        static void Outline(Rect r, Color c, float t)
        {
            EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, t), c);
            EditorGUI.DrawRect(new Rect(r.x, r.yMax - t, r.width, t), c);
            EditorGUI.DrawRect(new Rect(r.x, r.y, t, r.height), c);
            EditorGUI.DrawRect(new Rect(r.xMax - t, r.y, t, r.height), c);
        }

        static void Hatch(Rect r, Color c)
        {
            float t = Mathf.Max(1f, r.width * 0.12f);
            for (int i = 1; i <= 3; i++)
            {
                float k = i / 4f;
                EditorGUI.DrawRect(new Rect(r.x + r.width * k - t * 0.5f, r.y + r.height * (1f - k) - t * 0.5f, t, t), c);
            }
        }

        // ---------- мышь ----------

        void Mouse()
        {
            var e = Event.current;
            int id = GUIUtility.GetControlID(FocusType.Passive);
            var at = CellAt(e.mousePosition);
            switch (e.type)
            {
                case EventType.MouseMove:
                    if (at != hover) { hover = at; Repaint(); }
                    break;
                case EventType.ScrollWheel when layer >= ArenaLayout.Traps && gridRect.Contains(e.mousePosition):
                    Rotate(e.delta.y > 0 ? 1 : -1);
                    e.Use();
                    break;
                case EventType.MouseDown when at is Vector2Int c && (e.button == 0 || e.button == 1):
                    GUIUtility.hotControl = id;
                    GUIUtility.keyboardControl = 0;
                    Down(c, e.button);
                    e.Use();
                    break;
                case EventType.MouseDrag when GUIUtility.hotControl == id:
                    hover = at;
                    if (at is Vector2Int d) Drag(d);
                    e.Use();
                    break;
                case EventType.MouseUp when GUIUtility.hotControl == id:
                    GUIUtility.hotControl = 0;
                    Up();
                    e.Use();
                    break;
            }
        }

        void Down(Vector2Int c, int button)
        {
            strokeBefore = arena.ToJson();
            strokeChanged = false;
            stroke.Clear();
            switch (layer)
            {
                case ArenaLayout.Shape:
                    if (button != 0) return;
                    gesture = Gesture.Shape;
                    shapeAdd = !arena.In(c.x, c.y);
                    Drag(c);
                    break;
                case ArenaLayout.Surface:
                    gesture = Gesture.Surface;
                    shapeAdd = button == 0; // ЛКМ — положить, ПКМ — стереть
                    Drag(c);
                    break;
                default:
                {
                    var o = arena.ObjectAt(c.x, c.y, layer);
                    if (button == 1)
                    {
                        if (o != null) Act(() => arena.objects.Remove(o));
                        gesture = Gesture.None;
                        return;
                    }
                    if (o != null)
                    {
                        gesture = Gesture.Move;
                        moving = o;
                        moveFrom = new Vector2Int(o.x, o.y);
                        moveRotFrom = o.rot;
                        grab = c - moveFrom;
                    }
                    else
                    {
                        var pieces = ArenaPieces.OfLayer(layer);
                        gesture = Gesture.Place;
                        ghost = new ArenaObject { id = pieces[pieceIndex[layer]].Id, x = c.x, y = c.y, rot = rot };
                        grab = Vector2Int.zero;
                    }
                    Repaint();
                    break;
                }
            }
        }

        void Drag(Vector2Int c)
        {
            switch (gesture)
            {
                case Gesture.Shape:
                    if (!stroke.Add(c)) return; // каждая клетка — раз за штрих; курсор на месте ничего не делает
                    if (arena.In(c.x, c.y) == shapeAdd) return;
                    arena.SetIn(c.x, c.y, shapeAdd);
                    if (shapeAdd && arena.Surf(c.x, c.y) == ArenaLayout.NoSurface) arena.SetSurf(c.x, c.y, ArenaSurface.Floor.Code);
                    strokeChanged = true;
                    Changed();
                    break;
                case Gesture.Surface:
                {
                    if (!stroke.Add(c) || !arena.In(c.x, c.y)) return; // за пределы формы не красит
                    char code = shapeAdd ? ArenaSurface.All[surfaceIndex].Code : ArenaLayout.NoSurface;
                    if (arena.Surf(c.x, c.y) == code) return;
                    arena.SetSurf(c.x, c.y, code);
                    strokeChanged = true;
                    Changed();
                    break;
                }
                case Gesture.Place:
                    ghost.x = c.x - grab.x; ghost.y = c.y - grab.y;
                    Repaint();
                    break;
                case Gesture.Move:
                    moving.x = c.x - grab.x; moving.y = c.y - grab.y;
                    Repaint();
                    break;
            }
        }

        void Up()
        {
            switch (gesture)
            {
                case Gesture.Shape:
                case Gesture.Surface:
                    if (strokeChanged) Snapshot(strokeBefore);
                    break;
                case Gesture.Place:
                    if (CanPlace(ghost, null))
                    {
                        arena.objects.Add(ghost);
                        Snapshot(strokeBefore);
                        Changed();
                    }
                    else ShowNotification(new GUIContent("Сюда нельзя: нужен свободный пол внутри формы"));
                    ghost = null;
                    break;
                case Gesture.Move:
                    if (moving.x == moveFrom.x && moving.y == moveFrom.y && moving.rot == moveRotFrom) { }
                    else if (CanPlace(moving, moving)) { Snapshot(strokeBefore); Changed(); }
                    else
                    {
                        moving.x = moveFrom.x; moving.y = moveFrom.y; moving.rot = moveRotFrom;
                        ShowNotification(new GUIContent("Сюда нельзя: нужен свободный пол внутри формы"));
                    }
                    moving = null;
                    break;
            }
            gesture = Gesture.None;
            stroke.Clear();
            Repaint();
        }

        void CancelGesture()
        {
            if (moving != null) { moving.x = moveFrom.x; moving.y = moveFrom.y; moving.rot = moveRotFrom; }
            moving = null;
            ghost = null;
            gesture = Gesture.None;
            GUIUtility.hotControl = 0;
        }

        /// <summary>Объект целиком на полу внутри формы и ни с чем не пересекается (self — он сам, при переносе).</summary>
        bool CanPlace(ArenaObject o, ArenaObject self)
        {
            var r = ArenaLayout.Footprint(o);
            for (int y = r.yMin; y < r.yMax; y++)
                for (int x = r.xMin; x < r.xMax; x++)
                {
                    if (!arena.IsFloor(x, y)) return false;
                    foreach (var other in arena.objects)
                        if (other != self && ArenaLayout.Footprint(other).Contains(new Vector2Int(x, y))) return false;
                }
            return true;
        }
    }
}
