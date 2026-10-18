using System.Collections.Generic;
using UnityEngine;

namespace HydroSandbox
{
    /// <summary>
    /// Fills the solver's container with still sea: every lattice point between the terrain bed
    /// and sea level, jittered, since a perfect lattice survives in calm water and shows as
    /// terraced rows on the rendered surface.
    /// </summary>
    [RequireComponent(typeof(FluidSolver))]
    public class OceanFill : FluidInitialFill
    {
        [SerializeField] Terrain terrain;
        [SerializeField] float seaLevel;

        public void Configure(Terrain bed, float level)
        {
            terrain = bed;
            seaLevel = level;
        }

        public override void Generate(float spacing, List<Vector3> positions, List<Vector3> velocities)
        {
            Bounds box = GetComponent<FluidSolver>().Container;
            float half = spacing * 0.5f;
            var random = new System.Random(1);
            float jitter = spacing * 0.15f;
            for (float z = box.min.z + half; z < box.max.z; z += spacing)
            for (float x = box.min.x + half; x < box.max.x; x += spacing)
            {
                float bed = terrain != null ? terrain.SampleHeight(new Vector3(x, 0f, z)) + terrain.transform.position.y : box.min.y;
                for (float y = Mathf.Max(bed, box.min.y) + half; y < seaLevel; y += spacing)
                    positions.Add(new Vector3(x, y, z) + jitter * new Vector3(
                        (float)random.NextDouble() * 2f - 1f, (float)random.NextDouble() * 2f - 1f, (float)random.NextDouble() * 2f - 1f));
            }
        }
    }
}
