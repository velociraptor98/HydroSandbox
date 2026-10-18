using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace HydroSandbox
{
    public enum FluidSourceShape
    {
        Point,
        Line,
        Area,
        Rectangle,
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SpawnLayer
    {
        public Vector3 origin;
        public uint firstPoint;
        public Vector3 velocity;
        public uint patternStart;
        public Vector3 axisU;
        public uint patternCount;
        public Vector3 axisV;
        public uint fluidType;
    }

    /// <summary>
    /// Emits fluid along the transform's forward axis. The nozzle cross-section is filled with a
    /// square lattice at particle spacing, and one layer is released each time the stream has
    /// advanced one spacing, so new water enters at rest density. Flow rate therefore follows
    /// from nozzle size and speed.
    /// </summary>
    public class FluidSource : MonoBehaviour
    {
        public static readonly List<FluidSource> Active = new List<FluidSource>();

        [SerializeField] FluidSourceShape shape = FluidSourceShape.Area;

        [Tooltip("Line length, area diameter or rectangle width (along the transform's right), in meters. Ignored for Point.")]
        [SerializeField, Min(0f)] float size = 0.05f;
        [Tooltip("Rectangle height, along the transform's up axis, in meters.")]
        [SerializeField, Min(0f)] float rectangleHeight = 0.1f;

        [Tooltip("Exit speed along the transform's forward axis, in m/s.")]
        [SerializeField, Min(0.01f)] float speed = 1.5f;

        [Tooltip("Liquid (index into the solver's FluidMixing) of every particle this source emits.")]
        [SerializeField, Range(0, FluidMixing.MaxLiquids - 1)] uint fluidType;

        [SerializeField] bool pulsed;
        [SerializeField, Min(0.01f)] float pulsePeriod = 1f;
        [SerializeField, Range(0f, 1f)] float pulseDuty = 0.5f;

        float travel;
        float time;

        public int Liquid => (int)fluidType;

        public float Speed
        {
            get => speed;
            set => speed = Mathf.Max(0.01f, value);
        }

        public float FlowRateLitersPerSecond(float spacing)
        {
            int points = CountPattern(spacing);
            float dutyScale = pulsed ? pulseDuty : 1f;
            return points * spacing * spacing * speed * 1000f * dutyScale;
        }

        void OnEnable() => Active.Add(this);

        void OnDisable() => Active.Remove(this);

        internal void Emit(float dt, float spacing, List<SpawnLayer> layers, List<Vector2> pattern,
            ref int pointCount, int maxPoints)
        {
            time += dt;
            if (pulsed && Mathf.Repeat(time, pulsePeriod) >= pulseDuty * pulsePeriod)
            {
                travel = 0f;
                return;
            }

            travel += speed * dt;
            if (travel < spacing)
                return;

            int patternStart = pattern.Count;
            AppendPattern(spacing, pattern);
            int patternCount = pattern.Count - patternStart;

            Transform t = transform;
            Vector3 forward = t.forward;
            while (travel >= spacing)
            {
                travel -= spacing;
                if (pointCount + patternCount > maxPoints)
                    continue;
                layers.Add(new SpawnLayer
                {
                    origin = t.position + forward * travel,
                    firstPoint = (uint)pointCount,
                    velocity = forward * speed,
                    patternStart = (uint)patternStart,
                    axisU = t.right,
                    patternCount = (uint)patternCount,
                    axisV = t.up,
                    fluidType = fluidType,
                });
                pointCount += patternCount;
            }
        }

        int CountPattern(float spacing)
        {
            var points = new List<Vector2>();
            AppendPattern(spacing, points);
            return points.Count;
        }

        void AppendPattern(float spacing, List<Vector2> points)
        {
            int start = points.Count;
            float half = size * 0.5f;
            int m = Mathf.FloorToInt(half / spacing);
            switch (shape)
            {
                case FluidSourceShape.Line:
                    for (int i = -m; i <= m; i++)
                        points.Add(new Vector2(i * spacing, 0f));
                    break;
                case FluidSourceShape.Rectangle:
                {
                    int nu = Mathf.Max(1, Mathf.FloorToInt(size / spacing) + 1);
                    int nv = Mathf.Max(1, Mathf.FloorToInt(rectangleHeight / spacing) + 1);
                    for (int j = 0; j < nv; j++)
                    for (int i = 0; i < nu; i++)
                        points.Add(new Vector2((i - (nu - 1) * 0.5f) * spacing, (j - (nv - 1) * 0.5f) * spacing));
                    break;
                }
                case FluidSourceShape.Area:
                    // Offsetting even widths by half a spacing centers the lattice on the nozzle.
                    float offset = (Mathf.FloorToInt(size / spacing) % 2 == 0) ? 0.5f : 0f;
                    for (int j = -m - 1; j <= m; j++)
                    for (int i = -m - 1; i <= m; i++)
                    {
                        Vector2 q = new Vector2(i + offset, j + offset) * spacing;
                        if (q.sqrMagnitude <= half * half)
                            points.Add(q);
                    }
                    break;
            }
            if (points.Count == start)
                points.Add(Vector2.zero);
        }

        void OnDrawGizmos()
        {
            Gizmos.color = new Color(0.2f, 0.6f, 1f);
            Gizmos.matrix = transform.localToWorldMatrix;
            if (shape == FluidSourceShape.Line)
                Gizmos.DrawLine(Vector3.left * size * 0.5f, Vector3.right * size * 0.5f);
            else if (shape == FluidSourceShape.Rectangle)
                Gizmos.DrawWireCube(Vector3.zero, new Vector3(size, rectangleHeight, 0f));
            else
                Gizmos.DrawWireSphere(Vector3.zero, Mathf.Max(size * 0.5f, 0.01f));
            Gizmos.DrawLine(Vector3.zero, Vector3.forward * 0.15f);
        }
    }
}
