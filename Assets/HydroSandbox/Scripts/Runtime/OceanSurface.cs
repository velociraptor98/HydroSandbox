using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace HydroSandbox
{
    /// <summary>
    /// Draws the <see cref="OceanWaves"/> sea out to the horizon on a grid that follows the camera:
    /// evenly spaced around the camera, then stretching geometrically outward. The grid moves
    /// in steps of two cells, so its vertices stay on fixed world positions and the waves do not
    /// swim through it. It also publishes its material's foam look, which particle water in an
    /// ocean zone uses to carry the foam pattern on across the zone's edge.
    /// </summary>
    [RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
    public class OceanSurface : MonoBehaviour
    {
        [SerializeField, Range(32, 254)] int halfResolution = 160;
        [Tooltip("Cell size of the even inner grid, m.")]
        [SerializeField, Min(0.05f)] float innerSpacing = 0.35f;
        [Tooltip("Half the inner grid's cells; the rest stretch out to the far distance.")]
        [SerializeField, Range(8, 200)] int innerCells = 96;
        [SerializeField, Min(10f)] float farDistance = 6000f;
        [SerializeField] float seaLevel;

        static readonly int FoamTexture = Shader.PropertyToID("_FoamTexture");
        static readonly int FoamScale = Shader.PropertyToID("_FoamScale");
        static readonly int FoamColor = Shader.PropertyToID("_FoamColor");
        static readonly int GlobalFoamPattern = Shader.PropertyToID("_OceanFoamPattern");
        static readonly int GlobalFoamPatternScale = Shader.PropertyToID("_OceanFoamPatternScale");
        static readonly int GlobalFoamColor = Shader.PropertyToID("_OceanFoamColor");

        Mesh mesh;
        Camera view;

        void OnEnable()
        {
            mesh = BuildGrid();
            GetComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = GetComponent<MeshRenderer>();
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        void OnDisable()
        {
            if (mesh != null)
                Destroy(mesh);
        }

        void LateUpdate()
        {
            Material material = GetComponent<MeshRenderer>().sharedMaterial;
            if (material != null)
            {
                Shader.SetGlobalTexture(GlobalFoamPattern, material.GetTexture(FoamTexture));
                Shader.SetGlobalFloat(GlobalFoamPatternScale, material.GetFloat(FoamScale));
                Shader.SetGlobalColor(GlobalFoamColor, material.GetColor(FoamColor));
            }
            if (view == null)
                view = Camera.main;
            if (view == null)
                return;
            float step = innerSpacing * 2f;
            Vector3 p = view.transform.position;
            transform.SetPositionAndRotation(new Vector3(Mathf.Round(p.x / step) * step, seaLevel, Mathf.Round(p.z / step) * step), Quaternion.identity);
        }

        Mesh BuildGrid()
        {
            int n = halfResolution;
            int inner = Mathf.Min(innerCells, n - 1);
            float growth = SolveGrowth(n - inner, innerSpacing, farDistance - inner * innerSpacing);
            var offsets = new float[n + 1];
            float cell = innerSpacing;
            for (int i = 1; i <= n; i++)
            {
                if (i > inner)
                    cell *= growth;
                offsets[i] = offsets[i - 1] + cell;
            }

            int side = 2 * n + 1;
            var vertices = new List<Vector3>(side * side);
            for (int z = -n; z <= n; z++)
            for (int x = -n; x <= n; x++)
                vertices.Add(new Vector3(Mathf.Sign(x) * offsets[Mathf.Abs(x)], 0f, Mathf.Sign(z) * offsets[Mathf.Abs(z)]));

            var indices = new int[(side - 1) * (side - 1) * 6];
            int k = 0;
            for (int z = 0; z < side - 1; z++)
            for (int x = 0; x < side - 1; x++)
            {
                int i0 = z * side + x;
                int i1 = i0 + 1, i2 = i0 + side, i3 = i2 + 1;
                // Diagonals mirrored per quadrant keep the grid symmetric about the camera.
                bool flip = (x < n) != (z < n);
                if (flip)
                {
                    indices[k++] = i0; indices[k++] = i2; indices[k++] = i1;
                    indices[k++] = i1; indices[k++] = i2; indices[k++] = i3;
                }
                else
                {
                    indices[k++] = i0; indices[k++] = i2; indices[k++] = i3;
                    indices[k++] = i0; indices[k++] = i3; indices[k++] = i1;
                }
            }

            var grid = new Mesh { name = "OceanGrid", indexFormat = IndexFormat.UInt32 };
            grid.SetVertices(vertices);
            grid.SetIndices(indices, MeshTopology.Triangles, 0);
            // Displacement moves vertices up to several meters; never cull the sea.
            grid.bounds = new Bounds(Vector3.zero, new Vector3(farDistance * 2f, 200f, farDistance * 2f));
            return grid;
        }

        // Ratio r with innerSpacing * (r + r^2 + ... + r^count) = distance.
        static float SolveGrowth(int count, float spacing, float distance)
        {
            float lo = 1f, hi = 2f;
            for (int i = 0; i < 60; i++)
            {
                float mid = 0.5f * (lo + hi);
                float sum = 0f, term = 1f;
                for (int j = 0; j < count; j++)
                {
                    term *= mid;
                    sum += term;
                }
                if (spacing * sum < distance)
                    lo = mid;
                else
                    hi = mid;
            }
            return 0.5f * (lo + hi);
        }
    }
}
