using UnityEngine;

namespace HydroSandbox
{
    /// <summary>
    /// Makes the water collide with a Unity Terrain by uploading its heightmap once; the solver
    /// samples it on the GPU. Call <see cref="Refresh"/> after editing the terrain at runtime.
    /// </summary>
    public class FluidTerrainCollider : MonoBehaviour
    {
        public static FluidTerrainCollider ActiveTerrain { get; private set; }

        [SerializeField] Terrain terrain;
        [Tooltip("Fraction of tangential slip removed per bed contact. Applied in every collision pass (16 per frame at 4 substeps), so even 0.01 makes shallow water crawl; 0 gives realistic stream speeds.")]
        [SerializeField, Range(0f, 1f)] float friction = 0f;

        GraphicsBuffer heights;

        public float Friction => friction;
        public GraphicsBuffer HeightBuffer => heights;
        public int Resolution { get; private set; }
        public Vector3 Origin { get; private set; }
        public Vector3 Size { get; private set; }

        void OnEnable()
        {
            if (terrain == null)
                terrain = GetComponent<Terrain>();
            Refresh();
            ActiveTerrain = this;
        }

        void OnDisable()
        {
            if (ActiveTerrain == this)
                ActiveTerrain = null;
            heights?.Release();
            heights = null;
        }

        public void Refresh()
        {
            if (terrain == null || terrain.terrainData == null)
                return;
            TerrainData data = terrain.terrainData;
            int res = data.heightmapResolution;
            float[,] grid = data.GetHeights(0, 0, res, res);
            var flat = new float[res * res];
            for (int z = 0; z < res; z++)
            for (int x = 0; x < res; x++)
                flat[z * res + x] = grid[z, x];

            if (heights == null || heights.count != flat.Length)
            {
                heights?.Release();
                heights = new GraphicsBuffer(GraphicsBuffer.Target.Structured, flat.Length, 4);
            }
            heights.SetData(flat);
            Resolution = res;
            Origin = terrain.transform.position;
            Size = data.size;
        }
    }
}
