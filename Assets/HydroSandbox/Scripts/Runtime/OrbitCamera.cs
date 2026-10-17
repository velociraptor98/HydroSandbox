using UnityEngine;
using UnityEngine.InputSystem;

namespace HydroSandbox
{
    /// <summary>
    /// Orbit / pan / zoom camera for inspecting a simulation: left drag orbits, right or middle
    /// drag pans, the scroll wheel zooms. Ignores the mouse while an IMGUI control is in use.
    /// </summary>
    public class OrbitCamera : MonoBehaviour
    {
        [SerializeField] Vector3 target = Vector3.zero;
        [SerializeField, Min(0.1f)] float distance = 8f;
        [SerializeField] float yaw = -30f;
        [SerializeField, Range(-89f, 89f)] float pitch = 30f;
        [SerializeField, Min(0.01f)] float orbitSpeed = 0.25f;
        [SerializeField, Min(0.01f)] float zoomSpeed = 0.1f;
        [SerializeField] Vector2 distanceRange = new Vector2(0.5f, 40f);

        void LateUpdate()
        {
            Mouse mouse = Mouse.current;
            if (mouse != null && GUIUtility.hotControl == 0)
            {
                Vector2 delta = mouse.delta.ReadValue();
                if (mouse.leftButton.isPressed)
                {
                    yaw += delta.x * orbitSpeed;
                    pitch = Mathf.Clamp(pitch - delta.y * orbitSpeed, -89f, 89f);
                }
                else if (mouse.rightButton.isPressed || mouse.middleButton.isPressed)
                {
                    float panScale = distance * 0.0015f;
                    target -= (transform.right * delta.x + transform.up * delta.y) * panScale;
                }
                float scroll = mouse.scroll.ReadValue().y;
                if (Mathf.Abs(scroll) > 0.01f)
                    distance = Mathf.Clamp(distance * (1f - Mathf.Sign(scroll) * zoomSpeed), distanceRange.x, distanceRange.y);
            }

            ApplyTransform();
        }

        void ApplyTransform()
        {
            Quaternion rotation = Quaternion.Euler(pitch, yaw, 0f);
            transform.SetPositionAndRotation(target - rotation * Vector3.forward * distance, rotation);
        }

        public void Frame(Vector3 newTarget, float newDistance, float newYaw, float newPitch)
        {
            target = newTarget;
            distance = newDistance;
            yaw = newYaw;
            pitch = newPitch;
            ApplyTransform();
        }
    }
}
