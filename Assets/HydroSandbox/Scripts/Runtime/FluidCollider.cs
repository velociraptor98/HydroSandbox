using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace HydroSandbox
{
    public enum FluidColliderShape
    {
        Box,
        Sphere,
        Capsule,
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ColliderRecord
    {
        public Vector3 center;
        public uint shape;
        public Vector4 rotation;
        public Vector3 extents;
        public float radius;
        public Vector3 linearVelocity;
        public uint padding;
        public Vector3 angularVelocity;
        public float friction;
    }

    /// <summary>
    /// An obstacle the water collides with. Dimensions are in local space and follow the
    /// transform's scale, like Unity's own colliders. With a Rigidbody (on this object or a
    /// parent) and coupling enabled, the water also pushes the body back.
    /// </summary>
    public class FluidCollider : MonoBehaviour
    {
        public static readonly List<FluidCollider> Active = new List<FluidCollider>();

        static readonly Quaternion[] AxisRotations =
        {
            Quaternion.Euler(0f, 0f, 90f), // X: the capsule's local Y onto X
            Quaternion.identity,
            Quaternion.Euler(90f, 0f, 0f), // Z
        };

        [SerializeField] FluidColliderShape shape = FluidColliderShape.Sphere;
        [SerializeField] Vector3 center = Vector3.zero;
        [Tooltip("Box size (full extents).")]
        [SerializeField] Vector3 size = Vector3.one;
        [Tooltip("Sphere or capsule radius.")]
        [SerializeField, Min(0f)] float radius = 0.5f;
        [Tooltip("Capsule total height, including the end caps.")]
        [SerializeField, Min(0f)] float height = 2f;
        [Tooltip("Capsule axis: 0 = X, 1 = Y, 2 = Z.")]
        [SerializeField, Range(0, 2)] int direction = 1;
        [Tooltip("Fraction of tangential slip removed at contact (0 = frictionless, 1 = no-slip).")]
        [SerializeField, Range(0f, 1f)] float friction = 0.2f;
        [Tooltip("Let the water push the attached Rigidbody (buoyancy and drag emerge from contacts).")]
        [SerializeField] bool twoWayCoupling = true;

        Rigidbody body;

        /// <summary>The Rigidbody the water pushes, or null for a one-way obstacle.</summary>
        public Rigidbody CoupledBody => twoWayCoupling && body != null && !body.isKinematic ? body : null;

        void OnEnable()
        {
            body = GetComponentInParent<Rigidbody>();
            Active.Add(this);
        }

        void OnDisable() => Active.Remove(this);

        public Vector3 WorldCenter => transform.TransformPoint(center);

        internal ColliderRecord BuildRecord()
        {
            Transform t = transform;
            Vector3 scale = t.lossyScale;
            Quaternion rotation = t.rotation;
            Vector3 extents = Vector3.zero;
            float r = radius;
            switch (shape)
            {
                case FluidColliderShape.Box:
                    extents = Vector3.Scale(size, scale) * 0.5f;
                    break;
                case FluidColliderShape.Sphere:
                    r = radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
                    break;
                case FluidColliderShape.Capsule:
                {
                    Vector3 s = new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z));
                    float axisScale = s[direction];
                    float radialScale = Mathf.Max(s[(direction + 1) % 3], s[(direction + 2) % 3]);
                    r = radius * radialScale;
                    extents = new Vector3(0f, Mathf.Max(height * axisScale * 0.5f - r, 0f), 0f);
                    rotation *= AxisRotations[direction];
                    break;
                }
            }

            Vector3 linear = Vector3.zero;
            Vector3 angular = Vector3.zero;
            if (body != null)
            {
                linear = body.GetPointVelocity(WorldCenter);
                angular = body.angularVelocity;
            }

            return new ColliderRecord
            {
                center = WorldCenter,
                shape = (uint)shape,
                rotation = new Vector4(rotation.x, rotation.y, rotation.z, rotation.w),
                extents = extents,
                radius = r,
                linearVelocity = linear,
                angularVelocity = angular,
                friction = friction,
            };
        }

        public struct SurfaceSample
        {
            public Vector3 position;
            public Vector3 normal;
            public float area;
            public float volumeShare;
        }

        /// <summary>
        /// World-space points spread over the collider's surface, each carrying its share of the
        /// shape's surface area and volume (used for buoyancy and drag on coupled bodies).
        /// </summary>
        public void GetSurfaceSamples(List<SurfaceSample> output)
        {
            ColliderRecord r = BuildRecord();
            var rotation = new Quaternion(r.rotation.x, r.rotation.y, r.rotation.z, r.rotation.w);
            int start = output.Count;
            float area, volume;
            switch (shape)
            {
                case FluidColliderShape.Sphere:
                {
                    const int count = 24;
                    float golden = Mathf.PI * (3f - Mathf.Sqrt(5f));
                    for (int i = 0; i < count; i++)
                    {
                        float y = 1f - 2f * (i + 0.5f) / count;
                        float ring = Mathf.Sqrt(1f - y * y);
                        var n = new Vector3(Mathf.Cos(golden * i) * ring, y, Mathf.Sin(golden * i) * ring);
                        output.Add(new SurfaceSample { position = r.center + n * r.radius, normal = n });
                    }
                    area = 4f * Mathf.PI * r.radius * r.radius;
                    volume = 4f / 3f * Mathf.PI * r.radius * r.radius * r.radius;
                    break;
                }
                case FluidColliderShape.Capsule:
                {
                    float half = r.extents.y;
                    int rings = Mathf.Clamp(Mathf.RoundToInt(2f * half / Mathf.Max(r.radius, 1e-3f)) + 1, 2, 9);
                    const int around = 8;
                    for (int i = 0; i < rings; i++)
                    {
                        float y = Mathf.Lerp(-half, half, i / (rings - 1f));
                        for (int k = 0; k < around; k++)
                        {
                            float angle = k * Mathf.PI * 2f / around;
                            var n = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                            output.Add(new SurfaceSample
                            {
                                position = r.center + rotation * (new Vector3(0f, y, 0f) + n * r.radius),
                                normal = rotation * n,
                            });
                        }
                    }
                    foreach (float end in new[] { -1f, 1f })
                    {
                        var n = new Vector3(0f, end, 0f);
                        output.Add(new SurfaceSample { position = r.center + rotation * (n * (half + r.radius)), normal = rotation * n });
                    }
                    area = 2f * Mathf.PI * r.radius * 2f * half + 4f * Mathf.PI * r.radius * r.radius;
                    volume = Mathf.PI * r.radius * r.radius * 2f * half + 4f / 3f * Mathf.PI * r.radius * r.radius * r.radius;
                    break;
                }
                default:
                {
                    Vector3 e = r.extents;
                    for (int axis = 0; axis < 3; axis++)
                    for (int side = -1; side <= 1; side += 2)
                    for (int u = -1; u <= 1; u++)
                    for (int v = -1; v <= 1; v++)
                    {
                        var local = Vector3.zero;
                        local[axis] = side * e[axis];
                        local[(axis + 1) % 3] = u * e[(axis + 1) % 3] * 0.66f;
                        local[(axis + 2) % 3] = v * e[(axis + 2) % 3] * 0.66f;
                        var n = Vector3.zero;
                        n[axis] = side;
                        output.Add(new SurfaceSample { position = r.center + rotation * local, normal = rotation * n });
                    }
                    area = 8f * (e.x * e.y + e.y * e.z + e.z * e.x);
                    volume = 8f * e.x * e.y * e.z;
                    break;
                }
            }

            int added = output.Count - start;
            for (int i = start; i < output.Count; i++)
            {
                SurfaceSample sample = output[i];
                sample.area = area / added;
                sample.volumeShare = volume / added;
                output[i] = sample;
            }
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.2f, 0.9f, 1f);
            Gizmos.matrix = transform.localToWorldMatrix;
            switch (shape)
            {
                case FluidColliderShape.Box:
                    Gizmos.DrawWireCube(center, size);
                    break;
                case FluidColliderShape.Sphere:
                    Gizmos.DrawWireSphere(center, radius);
                    break;
                case FluidColliderShape.Capsule:
                {
                    Vector3 axis = Vector3.zero;
                    axis[direction] = Mathf.Max(height * 0.5f - radius, 0f);
                    Gizmos.DrawWireSphere(center + axis, radius);
                    Gizmos.DrawWireSphere(center - axis, radius);
                    break;
                }
            }
        }
    }
}
