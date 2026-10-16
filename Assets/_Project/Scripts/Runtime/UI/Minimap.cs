using Arena.Core;
using Arena.Sim;
using UnityEngine;
using UnityEngine.UI;

namespace Arena.UI
{
    /// <summary>Мини-карта этажа в духе Enter the Gungeon: окрестность Смерти крупно (PixelsPerCell на клетку), карта едет
    /// вместе с ней. Силуэт всех комнат этажа виден сразу — тусклая заливка с кромкой, соседние за дверями — ярче.
    /// Коридоры открываются там, где прошла Смерть (радиус 6 клеток), комната — целиком при входе. Босс — красный,
    /// выход — синий, запертая комната — оранжевая, Смерть — светлая точка.</summary>
    public sealed class Minimap
    {
        const float Size = 300f, PixelsPerCell = 5f;
        readonly RectTransform root;
        readonly RawImage image;
        readonly Image marker;
        Texture2D tex;
        EtgFloorLayout shownFloor;
        bool[] explored;
        bool dirty;
        int stateHash;

        public Minimap(Transform parent)
        {
            root = UiKit.Rect(parent, "Minimap", new Vector2(1f, 1f), new Vector2(-48f, -130f), new Vector2(Size, Size), new Vector2(1f, 1f));
            var frame = UiKit.Image(root, "Frame", new Color(0.05f, 0.04f, 0.08f, 0.55f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(Size, Size));
            frame.raycastTarget = false;
            var go = new GameObject("Map", typeof(RectTransform));
            var rt = (RectTransform)go.transform;
            rt.SetParent(root, false);
            rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
            image = go.AddComponent<RawImage>();
            image.raycastTarget = false;
            marker = UiKit.Image(rt, "You", new Color(1f, 1f, 0.9f), new Vector2(0f, 0f), Vector2.zero, new Vector2(8f, 8f));
            marker.rectTransform.localEulerAngles = new Vector3(0f, 0f, 45f);
            root.gameObject.SetActive(false);
        }

        public void Sync(ArenaSim sim)
        {
            bool on = sim.FloorMode;
            if (root.gameObject.activeSelf != on) root.gameObject.SetActive(on);
            if (!on) return;
            var f = sim.Floor;
            var b = f.Bounds;
            if (f != shownFloor)
            {
                shownFloor = f;
                if (tex != null) Object.Destroy(tex);
                tex = new Texture2D(b.width, b.height, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                image.texture = tex;
                image.rectTransform.sizeDelta = new Vector2(Size, Size);
                explored = new bool[b.width * b.height];
                dirty = true;
            }
            // открыть клетки вокруг Смерти
            var p = sim.Player.Pos;
            int px = Mathf.FloorToInt(p.x), py = Mathf.FloorToInt(p.y);
            for (int y = py - 6; y <= py + 6; y++)
                for (int x = px - 6; x <= px + 6; x++)
                {
                    if (!f.InBounds(x, y) || (x - px) * (x - px) + (y - py) * (y - py) > 36) continue;
                    int i = (x - b.x) + (y - b.y) * b.width;
                    if (!explored[i] && f.RoomAt(x, y) < 0) { explored[i] = true; dirty = true; }
                }
            int h = sim.ActiveRoom * 31;
            for (int i = 0; i < sim.RoomStates.Length; i++) h = h * 7 + (int)sim.RoomStates[i];
            if (h != stateHash) { stateHash = h; dirty = true; }
            if (dirty) { Redraw(sim); dirty = false; }
            // окно вокруг Смерти: края этажа прозрачные (запас пустых клеток), за краем текстура повторяет край
            float half = Size / PixelsPerCell * 0.5f;
            image.uvRect = new Rect((p.x - b.x - half) / b.width, (p.y - b.y - half) / b.height, 2f * half / b.width, 2f * half / b.height);
            marker.rectTransform.anchoredPosition = new Vector2(Size * 0.5f, Size * 0.5f);
        }

        void Redraw(ArenaSim sim)
        {
            var f = sim.Floor;
            var b = f.Bounds;
            var px = new Color32[b.width * b.height];
            var clear = new Color32(0, 0, 0, 0);
            for (int y = 0; y < b.height; y++)
                for (int x = 0; x < b.width; x++)
                {
                    int gx = b.x + x, gy = b.y + y, i = x + y * b.width;
                    var c = f.At(gx, gy);
                    int r = f.RoomAt(gx, gy);
                    var st = r >= 0 ? sim.RoomStates[r] : ArenaSim.FloorRoomState.Unseen;
                    bool open = r >= 0 ? st is ArenaSim.FloorRoomState.Visited or ArenaSim.FloorRoomState.Active or ArenaSim.FloorRoomState.Cleared : explored[i];
                    if (!open && r < 0) { px[i] = clear; continue; }
                    if (!open)
                    {
                        // комната, где Смерть не была, — силуэт: тусклая заливка и кромка по границе её клеток (у арен края —
                        // многоугольник пола, а не стены). Соседняя за дверью — ярче, остальные — тусклее
                        if (c is not (EtgCell.Floor or EtgCell.Door or EtgCell.Wall)) { px[i] = clear; continue; }
                        bool edge = f.RoomAt(gx + 1, gy) != r || f.RoomAt(gx - 1, gy) != r || f.RoomAt(gx, gy + 1) != r || f.RoomAt(gx, gy - 1) != r;
                        bool near = st == ArenaSim.FloorRoomState.Seen;
                        px[i] = edge ? (near ? new Color32(165, 162, 185, 170) : new Color32(130, 126, 152, 120))
                                     : (near ? new Color32(110, 106, 130, 80) : new Color32(90, 86, 110, 45));
                        continue;
                    }
                    var kind = r >= 0 ? f.Rooms[r].Kind : EtgRoomKind.Connector;
                    Color32 floor = kind switch
                    {
                        EtgRoomKind.Boss => new Color32(220, 90, 90, 230),
                        EtgRoomKind.Exit => new Color32(110, 160, 255, 230),
                        EtgRoomKind.Reward => new Color32(240, 210, 100, 230),
                        _ => st == ArenaSim.FloorRoomState.Active ? new Color32(240, 150, 70, 230) : new Color32(200, 196, 186, 210),
                    };
                    px[i] = c switch
                    {
                        EtgCell.Floor or EtgCell.Door => floor,
                        EtgCell.Wall => new Color32(60, 56, 70, 220),
                        EtgCell.Pit => new Color32(20, 18, 24, 200),
                        _ => clear,
                    };
                }
            tex.SetPixels32(px);
            tex.Apply();
        }
    }
}
