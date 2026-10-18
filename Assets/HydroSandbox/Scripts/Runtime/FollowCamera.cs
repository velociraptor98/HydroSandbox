using UnityEngine;

namespace HydroSandbox
{
    /// <summary>Keeps this object at an offset from the main camera's position (not its rotation): rain around the viewer.</summary>
    public class FollowCamera : MonoBehaviour
    {
        [SerializeField] Vector3 offset = new Vector3(0f, 12f, 0f);

        Camera view;

        void LateUpdate()
        {
            if (view == null)
                view = Camera.main;
            if (view != null)
                transform.position = view.transform.position + offset;
        }
    }
}
