using UnityEngine;

namespace Arena.View
{
    /// <summary>Ортографическая камера сверху под 30° с поворотом 45°: пол виден ромбом, как комнаты Hades.</summary>
    public sealed class CameraRig
    {
        public const float Pitch = 30f, Yaw = 45f, Distance = 30f;
        public readonly Camera Cam;
        Vector3 focus;
        float shake, shakeAmp, shakeT, shakeDur, baseSize = 7.2f;
        /// <summary>Приближение поверх зума комнаты (ChargeCameraMotion Ω-атаки).</summary>
        public float ZoomMul = 1f;

        public CameraRig(Camera cam)
        {
            Cam = cam;
            cam.orthographic = true;
            cam.orthographicSize = 7.2f;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 80f;
            cam.transform.rotation = Quaternion.Euler(Pitch, Yaw, 0f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.62f, 0.78f, 0.74f);
        }

        /// <summary>Масштаб как в Hades: при зуме 1 экран 1080p показывает 1080 пикселей мира по высоте,
        /// это 1080 × 4.1 / 540 = 8.2 юнита, то есть половина высоты ортокамеры 4.1. Зум комнаты Hades (ZoomFraction) — делитель.</summary>
        public void SetHadesZoom(float zoom) { baseSize = 4.1f / Mathf.Max(0.3f, zoom); Cam.orthographicSize = baseSize; }

        public void Snap(Vector3 target) { focus = target; Apply(); }

        public void Shake(float amount) => shake = Mathf.Max(shake, amount);

        /// <summary>Тряска Hades (Distance в юнитах, Duration): затухает до нуля за duration.</summary>
        public void Shake(float amount, float duration)
        {
            if (amount <= 0f || duration <= 0f) return;
            if (shakeT < shakeDur && shakeAmp * (1f - shakeT / shakeDur) > amount) return;
            shakeAmp = amount; shakeDur = duration; shakeT = 0f;
        }

        public void Follow(Vector3 target, float dt)
        {
            focus = Vector3.Lerp(focus, target, 1f - Mathf.Exp(-dt * 6f));
            shake = Mathf.Max(0f, shake - dt * 1.5f);
            shakeT += dt;
            Cam.orthographicSize = baseSize / Mathf.Max(0.1f, ZoomMul);
            Apply();
        }

        void Apply()
        {
            float amp = Mathf.Max(shake, shakeT < shakeDur ? shakeAmp * (1f - shakeT / shakeDur) : 0f);
            var off = amp > 0f ? (Vector3)(Random.insideUnitCircle * amp) : Vector3.zero;
            Cam.transform.position = focus - Cam.transform.forward * Distance + Cam.transform.rotation * off;
        }
    }
}
