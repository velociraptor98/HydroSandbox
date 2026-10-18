using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace HydroSandbox
{
    [StructLayout(LayoutKind.Sequential)]
    struct TankHole
    {
        public Vector3 center;
        public float radius;
        public uint wall;
        public uint pad0, pad1, pad2;
    }

    /// <summary>
    /// An open-topped, axis-aligned glass tank the water collides with from both sides, so it can
    /// hold liquid inside a larger solver container. Holes drilled in the side walls at runtime
    /// let the liquid leak out. The solver picks up the enabled tank automatically.
    /// </summary>
    public class FluidTank : MonoBehaviour
    {
        public const int MaxHoles = 32;

        public static FluidTank Active { get; private set; }

        [Tooltip("World-space interior of the tank: liquid sits on its bottom face, the top is open.")]
        [SerializeField] Bounds interior = new Bounds(new Vector3(0f, 0.3f, 0f), new Vector3(1f, 0.55f, 0.4f));
        [SerializeField, Min(0.001f)] float wallThickness = 0.01f;
        [Tooltip("Drawn around every hole, on both faces of the glass and along its bore.")]
        [SerializeField] Material holeEdgeMaterial;

        readonly List<TankHole> holes = new List<TankHole>();
        readonly List<GameObject> holeEdges = new List<GameObject>();
        GraphicsBuffer holeBuffer;
        bool holesDirty = true;

        public Bounds Interior => interior;
        public float WallThickness => wallThickness;
        public int HoleCount => holes.Count;
        internal GraphicsBuffer HoleBuffer => holeBuffer;

        public void Configure(Bounds tankInterior, float thickness, Material edgeMaterial)
        {
            interior = tankInterior;
            wallThickness = thickness;
            holeEdgeMaterial = edgeMaterial;
        }

        void OnEnable()
        {
            holeBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, MaxHoles, Marshal.SizeOf<TankHole>());
            holesDirty = true;
            Active = this;
        }

        void OnDisable()
        {
            if (Active == this)
                Active = null;
            holeBuffer?.Release();
            holeBuffer = null;
        }

        /// <summary>
        /// Drills a round hole through the side wall at <paramref name="point"/>; <paramref name="normal"/>
        /// (a surface normal of that wall) picks the wall. The floor cannot be drilled. Once
        /// <see cref="MaxHoles"/> exist, the oldest is sealed to make room.
        /// </summary>
        public bool TryDrill(Vector3 point, Vector3 normal, float radius)
        {
            if (!TryPlaceHole(point, normal, radius, out TankHole hole))
                return false;
            if (holes.Count >= MaxHoles)
            {
                holes.RemoveAt(0);
                DestroyEdge(holeEdges[0]);
                holeEdges.RemoveAt(0);
            }
            holes.Add(hole);
            holeEdges.Add(CreateHoleEdge(hole, holeEdgeMaterial, "Hole"));
            holesDirty = true;
            return true;
        }

        public void SealAll()
        {
            holes.Clear();
            foreach (GameObject edge in holeEdges)
                DestroyEdge(edge);
            holeEdges.Clear();
            holesDirty = true;
        }

        static void DestroyEdge(GameObject edge)
        {
            Destroy(edge.GetComponent<MeshFilter>().sharedMesh);
            Destroy(edge);
        }

        /// <summary>The hole <see cref="TryDrill"/> would make, as a center on the wall's midplane and the wall's outward axis.</summary>
        public bool TryPlaceHole(Vector3 point, Vector3 normal, float radius, out Vector3 center, out Vector3 axis)
        {
            bool placed = TryPlaceHole(point, normal, radius, out TankHole hole);
            center = hole.center;
            axis = WallAxis(hole.wall);
            return placed;
        }

        // Walls: 0 = -X, 1 = +X, 2 = -Z, 3 = +Z (matches ResolveTank).
        bool TryPlaceHole(Vector3 point, Vector3 normal, float radius, out TankHole hole)
        {
            hole = default;
            float ax = Mathf.Abs(normal.x), ay = Mathf.Abs(normal.y), az = Mathf.Abs(normal.z);
            if (ay > ax && ay > az)
                return false;
            int axis = ax > az ? 0 : 2;
            bool max = point[axis] > interior.center[axis];
            Vector3 c = point;
            c[axis] = max ? interior.max[axis] + wallThickness * 0.5f : interior.min[axis] - wallThickness * 0.5f;
            int lateral = 2 - axis;
            c[lateral] = Mathf.Clamp(c[lateral], interior.min[lateral], interior.max[lateral]);
            c.y = Mathf.Clamp(c.y, interior.min.y, interior.max.y);
            hole = new TankHole
            {
                center = c,
                radius = radius,
                wall = (uint)((axis == 0 ? 0 : 2) + (max ? 1 : 0)),
            };
            return true;
        }

        static Vector3 WallAxis(uint wall) => wall < 2 ? Vector3.right : Vector3.forward;

        internal void UploadHoles(UnityEngine.Rendering.CommandBuffer cmd)
        {
            if (!holesDirty || holeBuffer == null)
                return;
            if (holes.Count > 0)
                cmd.SetBufferData(holeBuffer, holes);
            holesDirty = false;
        }

        GameObject CreateHoleEdge(TankHole hole, Material material, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.transform.SetPositionAndRotation(hole.center, Quaternion.LookRotation(WallAxis(hole.wall)));
            go.AddComponent<MeshFilter>().sharedMesh = HoleEdgeMesh(hole.radius, wallThickness, holes.Count + 1);
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return go;
        }

        /// <summary>
        /// The ground edge of a drilled hole, in a frame whose z is the wall's thickness axis: a
        /// chipped ring on each face of the glass and the bore between them.
        /// </summary>
        public static Mesh HoleEdgeMesh(float radius, float thickness, int seed)
        {
            const int segments = 40;
            var random = new System.Random(seed);
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var triangles = new List<int>();
            float halfT = thickness * 0.5f + 0.0005f;
            var chips = new float[segments];
            for (int i = 0; i < segments; i++)
                chips[i] = radius * (0.12f + 0.25f * (float)random.NextDouble());

            foreach (float side in new[] { -1f, 1f })
            {
                int start = vertices.Count;
                for (int i = 0; i < segments; i++)
                {
                    float a = i * Mathf.PI * 2f / segments;
                    var dir = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                    vertices.Add(dir * radius + Vector3.forward * (side * halfT));
                    vertices.Add(dir * (radius + chips[i]) + Vector3.forward * (side * halfT));
                    normals.Add(Vector3.forward * side);
                    normals.Add(Vector3.forward * side);
                }
                for (int i = 0; i < segments; i++)
                {
                    int a0 = start + i * 2, b0 = start + ((i + 1) % segments) * 2;
                    if (side > 0f)
                        triangles.AddRange(new[] { a0, a0 + 1, b0 + 1, a0, b0 + 1, b0 });
                    else
                        triangles.AddRange(new[] { a0, b0 + 1, a0 + 1, a0, b0, b0 + 1 });
                }
            }

            int bore = vertices.Count;
            for (int i = 0; i < segments; i++)
            {
                float a = i * Mathf.PI * 2f / segments;
                var dir = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                vertices.Add(dir * radius - Vector3.forward * halfT);
                vertices.Add(dir * radius + Vector3.forward * halfT);
                normals.Add(-dir);
                normals.Add(-dir);
            }
            for (int i = 0; i < segments; i++)
            {
                int a0 = bore + i * 2, b0 = bore + ((i + 1) % segments) * 2;
                triangles.AddRange(new[] { a0, a0 + 1, b0 + 1, a0, b0 + 1, b0 });
            }

            var mesh = new Mesh { name = "TankHoleEdge" };
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
            return mesh;
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.6f, 0.9f, 1f);
            Gizmos.DrawWireCube(interior.center, interior.size);
        }
    }
}
