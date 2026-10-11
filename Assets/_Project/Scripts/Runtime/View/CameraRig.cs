using UnityEngine;

namespace Arena.View
{
    /// <summary>Ортографическая камера сверху под 30° с поворотом 45°: пол виден ромбом, как комнаты Hades.</summary>
    public sealed class CameraRig
    {
        public const float Pitch = 30f, Yaw = 45f, Distance = 30f;
        public readonly Camera Cam;
        Vector3 focus;
        float shake;

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

        public void Snap(Vector3 target) { focus = target; Apply(); }

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
