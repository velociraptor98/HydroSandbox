using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace HydroSandbox
{
    [StructLayout(LayoutKind.Sequential)]
    struct SinkRecord
    {
        public Vector3 center;
        public uint budget;
        public Vector3 axisX;
        public float pad0;
        public Vector3 axisY;
        public float pad1;
        public Vector3 axisZ;
        public float pad2;
    }

    /// <summary>
    /// Removes particles inside the transform's unit box (scaled by the transform) and returns
    /// them to the solver's pool.
    /// </summary>
    public class FluidSink : MonoBehaviour
    {
        public static readonly List<FluidSink> Active = new List<FluidSink>();

        [Tooltip("Liters per second this sink can remove. 0 removes everything that enters.")]
        [SerializeField, Min(0f)] float drainRate;

        float budgetCarry;

        public float DrainRate
        {
            get => drainRate;
            set => drainRate = Mathf.Max(0f, value);
        }

        void OnEnable() => Active.Add(this);

        void OnDisable() => Active.Remove(this);

        internal SinkRecord BuildRecord(float dt, float spacing)
        {
            uint budget = uint.MaxValue;
            if (drainRate > 0f)
            {
                budgetCarry += drainRate * 0.001f / (spacing * spacing * spacing) * dt;
                budget = (uint)Mathf.FloorToInt(budgetCarry);
                budgetCarry -= budget;
            }

            Transform t = transform;
            Vector3 scale = t.lossyScale;
            return new SinkRecord
            {
                center = t.position,
                budget = budget,
                axisX = t.right / Mathf.Max(scale.x, 1e-5f),
                axisY = t.up / Mathf.Max(scale.y, 1e-5f),
                axisZ = t.forward / Mathf.Max(scale.z, 1e-5f),
            };
        }

        void OnDrawGizmos()
        {
            Gizmos.color = new Color(1f, 0.5f, 0.1f);
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawWireCube(Vector3.zero, Vector3.one);
        }
    }
}
