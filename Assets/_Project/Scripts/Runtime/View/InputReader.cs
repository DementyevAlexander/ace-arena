using Arena.Sim;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Arena.View
{
    /// <summary>Клавиатура+мышь и геймпад → SimInput. Режим прицела переключается по последнему устройству, как в Hades.
    /// Раскладка Hades II (по таблицам в Hades2.exe): ЛКМ — атака, ПКМ — спецатака, Q — каст, Space и LeftShift — рывок;
    /// геймпад: X / Y / B / A, бег — левый стик и крестовина.</summary>
    public sealed class InputReader
    {
        public bool UseMouse = true;
        int pressedAcc, releasedAcc;
        Vector2 lastMouse;
        int stickX, stickY;

        /// <summary>Меню (кадр): шаг по горизонтали и вертикали (−1, 0, 1) — крестовина, стик (по одному шагу за наклон), стрелки;
        /// A / Enter — выбрать, B / Backspace — назад, Y — второе действие (влить душу), X — третье.</summary>
        public int NavX, NavY;
        /// <summary>Перелистнуть награду на экране выбора: Q / E, LB / RB (−1 / +1).</summary>
        public int NavPage;
        public bool NavConfirm, NavBack, NavAlt, NavX3;

        /// <summary>Вызывается каждый кадр: копит нажатия до следующего шага симуляции.</summary>
        public void Poll()
        {
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            var pad = Gamepad.current;
            if (mouse != null)
            {
                var mp = mouse.position.ReadValue();
                if ((mp - lastMouse).sqrMagnitude > 4f || mouse.leftButton.wasPressedThisFrame || mouse.rightButton.wasPressedThisFrame) UseMouse = true;
                lastMouse = mp;
            }
            if (kb != null && kb.anyKey.wasPressedThisFrame) UseMouse = true;
            if (pad != null && (pad.leftStick.ReadValue().sqrMagnitude > 0.1f || AnyPadButton(pad) || pad.dpad.ReadValue().sqrMagnitude > 0.1f)) UseMouse = false;
            PollNav(kb, pad);

            for (int i = 0; i < 4; i++)
            {
                if (Pressed(i, kb, mouse, pad)) pressedAcc |= 1 << i;
                if (Released(i, kb, mouse, pad)) releasedAcc |= 1 << i;
            }
        }

        void PollNav(Keyboard kb, Gamepad pad)
        {
            NavX = NavY = NavPage = 0;
            NavConfirm = NavBack = NavAlt = NavX3 = false;
            if (pad != null)
            {
                var st = pad.leftStick.ReadValue();
                int zx = st.x > 0.6f ? 1 : st.x < -0.6f ? -1 : 0, zy = st.y > 0.6f ? 1 : st.y < -0.6f ? -1 : 0;
                if (zx != 0 && zx != stickX) NavX = zx;
                if (zy != 0 && zy != stickY) NavY = zy;
                stickX = zx; stickY = zy;
                if (pad.dpad.left.wasPressedThisFrame) NavX = -1;
                if (pad.dpad.right.wasPressedThisFrame) NavX = 1;
                if (pad.dpad.up.wasPressedThisFrame) NavY = 1;
                if (pad.dpad.down.wasPressedThisFrame) NavY = -1;
                NavConfirm = pad.buttonSouth.wasPressedThisFrame;
                NavBack = pad.buttonEast.wasPressedThisFrame;
                NavAlt = pad.buttonNorth.wasPressedThisFrame;
                NavX3 = pad.buttonWest.wasPressedThisFrame;
                if (pad.leftShoulder.wasPressedThisFrame) NavPage = -1;
                if (pad.rightShoulder.wasPressedThisFrame) NavPage = 1;
            }
            if (kb != null)
            {
                if (kb.leftArrowKey.wasPressedThisFrame) NavX = -1;
                if (kb.rightArrowKey.wasPressedThisFrame) NavX = 1;
                if (kb.upArrowKey.wasPressedThisFrame) NavY = 1;
                if (kb.downArrowKey.wasPressedThisFrame) NavY = -1;
                NavConfirm |= kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame;
                NavBack |= kb.backspaceKey.wasPressedThisFrame;
                if (kb.qKey.wasPressedThisFrame) NavPage = -1;
                if (kb.eKey.wasPressedThisFrame) NavPage = 1;
            }
        }

        /// <summary>Сбрасывает накопленные нажатия (после паузы и меню).</summary>
        public void Flush() => pressedAcc = releasedAcc = 0;

        static bool AnyPadButton(Gamepad p) =>
            p.buttonWest.wasPressedThisFrame || p.buttonNorth.wasPressedThisFrame || p.buttonEast.wasPressedThisFrame || p.buttonSouth.wasPressedThisFrame;

        static bool Pressed(int i, Keyboard kb, Mouse m, Gamepad p) => i switch
        {
            0 => (m != null && m.leftButton.wasPressedThisFrame) || (p != null && p.buttonWest.wasPressedThisFrame),
            1 => (m != null && m.rightButton.wasPressedThisFrame) || (p != null && p.buttonNorth.wasPressedThisFrame),
            2 => (kb != null && kb.qKey.wasPressedThisFrame) || (p != null && p.buttonEast.wasPressedThisFrame),
            _ => (kb != null && (kb.spaceKey.wasPressedThisFrame || kb.leftShiftKey.wasPressedThisFrame)) || (p != null && p.buttonSouth.wasPressedThisFrame),
        };

        static bool Held(int i, Keyboard kb, Mouse m, Gamepad p) => i switch
        {
            0 => (m != null && m.leftButton.isPressed) || (p != null && p.buttonWest.isPressed),
            1 => (m != null && m.rightButton.isPressed) || (p != null && p.buttonNorth.isPressed),
            2 => (kb != null && kb.qKey.isPressed) || (p != null && p.buttonEast.isPressed),
            _ => (kb != null && (kb.spaceKey.isPressed || kb.leftShiftKey.isPressed)) || (p != null && p.buttonSouth.isPressed),
        };

        static bool Released(int i, Keyboard kb, Mouse m, Gamepad p) => i switch
        {
            0 => (m != null && m.leftButton.wasReleasedThisFrame) || (p != null && p.buttonWest.wasReleasedThisFrame),
            1 => (m != null && m.rightButton.wasReleasedThisFrame) || (p != null && p.buttonNorth.wasReleasedThisFrame),
            2 => (kb != null && kb.qKey.wasReleasedThisFrame) || (p != null && p.buttonEast.wasReleasedThisFrame),
            _ => (kb != null && (kb.spaceKey.wasReleasedThisFrame || kb.leftShiftKey.wasReleasedThisFrame)) || (p != null && p.buttonSouth.wasReleasedThisFrame),
        };

        /// <summary>Собирает ввод для шага. Экранные направления переводятся в мир по повороту камеры:
        /// при наклоне 30° клавиатурная диагональ идёт под ~26.6° к горизонтали — как в Hades.</summary>
        public SimInput Build(Camera cam, bool firstStep)
        {
            var kb = Keyboard.current;
            var mouse = Mouse.current;
            var pad = Gamepad.current;
            var input = new SimInput { UseMouse = UseMouse };

            Vector2 screen = Vector2.zero;
            if (kb != null)
            {
                if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) screen.x -= 1f;
                if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) screen.x += 1f;
                if (kb.wKey.isPressed || kb.upArrowKey.isPressed) screen.y += 1f;
                if (kb.sKey.isPressed || kb.downArrowKey.isPressed) screen.y -= 1f;
            }
            if (pad != null)
            {
                var st = pad.leftStick.ReadValue();
                if (st.sqrMagnitude > 0.2f * 0.2f) screen = st; // радиальная мёртвая зона
                else
                {
                    var dp = pad.dpad.ReadValue(); // в Hades II бег — и стиком, и крестовиной
                    if (dp.sqrMagnitude > 0.1f) screen = dp;
                }
            }
            input.Move = ScreenToWorld(cam, screen);

            if (UseMouse && mouse != null && cam != null)
            {
                var ray = cam.ScreenPointToRay(mouse.position.ReadValue());
                if (Mathf.Abs(ray.direction.y) > 0.0001f)
                {
                    float t = -ray.origin.y / ray.direction.y;
                    var hit = ray.origin + ray.direction * t;
                    input.Cursor = new Vector2(hit.x, hit.z);
                    input.HasCursor = true;
                }
            }

            for (int i = 0; i < 4; i++) if (Held(i, kb, mouse, pad)) input.Held |= 1 << i;
            if (firstStep)
            {
                input.Pressed = pressedAcc;
                input.Released = releasedAcc;
                pressedAcc = releasedAcc = 0;
            }
            return input;
        }

        static Vector2 ScreenToWorld(Camera cam, Vector2 s)
        {
            if (s.sqrMagnitude < 0.0001f || cam == null) return Vector2.zero;
            var f = cam.transform.forward; f.y = 0f; f.Normalize();
            var r = cam.transform.right; r.y = 0f; r.Normalize();
            var w = r * s.x + f * s.y;
            var v = new Vector2(w.x, w.z);
            return v.sqrMagnitude > 1f ? v.normalized : v;
        }

        public bool DeckPressed =>
            (Keyboard.current?.tabKey.wasPressedThisFrame ?? false) || (Gamepad.current?.selectButton.wasPressedThisFrame ?? false);

        public bool PausePressed =>
            (Keyboard.current?.escapeKey.wasPressedThisFrame ?? false) || (Gamepad.current?.startButton.wasPressedThisFrame ?? false);

        public bool RestartPressed => Keyboard.current?.rKey.wasPressedThisFrame ?? false;

        /// <summary>Перебросить награды пятачка, стоя рядом: F; на геймпаде — LB (в бою свободна).</summary>
        public bool RerollPressed =>
            (Keyboard.current?.fKey.wasPressedThisFrame ?? false) || (Gamepad.current?.leftShoulder.wasPressedThisFrame ?? false);

        /// <summary>«Взаимодействие» — подобрать карточку с земли: E, как в Hades; на геймпаде — RB (все четыре кнопки
        /// справа заняты скиллами).</summary>
        public bool InteractPressed =>
            (Keyboard.current?.eKey.wasPressedThisFrame ?? false) || (Gamepad.current?.rightShoulder.wasPressedThisFrame ?? false);
    }
}
