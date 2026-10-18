using UnityEngine;

namespace HydroSandbox
{
    /// <summary>
    /// Turns a lighthouse's optic: the beams (and the spot lights inside them) are children of
    /// this transform, which spins about its up axis.
    /// </summary>
    public class LighthouseLamp : MonoBehaviour
    {
        [Tooltip("Seconds per revolution.")]
        [SerializeField, Min(0.5f)] float period = 10f;

        void Update() => transform.Rotate(0f, 360f / period * Time.deltaTime, 0f, Space.Self);
    }
}
