using UnityEngine;

namespace Arena.View
{
    /// <summary>Ортографическая камера сверху под 30° с поворотом 45°: пол виден ромбом, как комнаты Hades.
    /// Масштаб — как картинка Hades: при зуме 1 по высоте экрана 1080 ед. Hades, то есть полувысота 540 ед. = 4.1 юнита.</summary>
    public sealed class CameraRig
    {
        public const float Pitch = 30f, Yaw = 45f, Distance = 30f;
        public static readonly float BaseSize = Arena.Core.Hades.U(540f);
        public readonly Camera Cam;
        Vector3 focus;
        float shake;

        public CameraRig(Camera cam)
        {
            Cam = cam;
            cam.orthographic = true;
            cam.orthographicSize = BaseSize;
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 80f;
            cam.transform.rotation = Quaternion.Euler(Pitch, Yaw, 0f);
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.62f, 0.78f, 0.74f);
        }

        public void Snap(Vector3 target) { focus = target; Apply(); }

        /// <summary>ZoomFraction комнаты Hades: меньше 1 — камера дальше.</summary>
        public void SetZoom(float zoom) => Cam.orthographicSize = BaseSize / Mathf.Clamp(zoom <= 0f ? 1f : zoom, 0.4f, 2f);

        public void Shake(float amount) => shake = Mathf.Max(shake, amount);

        public void Follow(Vector3 target, float dt)
        {
            focus = Vector3.Lerp(focus, target, 1f - Mathf.Exp(-dt * 6f));
            shake = Mathf.Max(0f, shake - dt * 1.5f);
            Apply();
        }

        void Apply()
        {
            var off = shake > 0f ? (Vector3)(Random.insideUnitCircle * shake) : Vector3.zero;
            Cam.transform.position = focus - Cam.transform.forward * Distance + Cam.transform.rotation * off;
        }
    }
}
