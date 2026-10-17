using System.Collections.Generic;
using UnityEngine;

namespace HydroSandbox
{
    /// <summary>
    /// Starts a river already flowing: fills the channel along a centerline polyline, from the
    /// terrain surface up to <see cref="depth"/> above the channel bed, with particles moving
    /// downstream at <see cref="speed"/>. Points overlapping physics colliders (boulders, logs)
    /// are skipped; particles placed inside an obstacle would be blasted out on the first step.
    /// </summary>
    public class RiverFill : FluidInitialFill
    {
        [Tooltip("Channel centerline in world space, upstream first. Heights are ignored; the bed is the lowest terrain point across the channel.")]
        [SerializeField] Vector3[] centerline = System.Array.Empty<Vector3>();
        [SerializeField] Terrain terrain;
        [Tooltip("Width of the strip to fill, centered on the centerline.")]
        [SerializeField, Min(0f)] float width = 1.2f;
        [Tooltip("Water depth above the local channel bed.")]
        [SerializeField, Min(0f)] float depth = 0.15f;
        [Tooltip("Initial downstream speed, in m/s.")]
        [SerializeField] float speed = 0.8f;

        public void Configure(Terrain targetTerrain, Vector3[] points, float fillWidth, float fillDepth, float fillSpeed)
        {
            terrain = targetTerrain;
            centerline = points;
            width = fillWidth;
            depth = fillDepth;
            speed = fillSpeed;
        }

        public override void Generate(float spacing, List<Vector3> positions, List<Vector3> velocities)
        {
            if (terrain == null || centerline.Length < 2)
                return;
            float terrainY = terrain.transform.position.y;
            float half = width * 0.5f;
            float clearance = spacing * 0.5f;
            int obstacleMask = ~0;
            Physics.SyncTransforms();

            for (int s = 0; s + 1 < centerline.Length; s++)
            {
                Vector3 a = centerline[s];
                Vector3 b = centerline[s + 1];
                Vector3 along = new Vector3(b.x - a.x, 0f, b.z - a.z);
                float length = along.magnitude;
                if (length < 1e-4f)
                    continue;
                along /= length;
                Vector3 across = new Vector3(-along.z, 0f, along.x);
                // Exclusive end so consecutive segments do not double up at shared points.
                for (float t = 0f; t < length; t += spacing)
                {
                    Vector3 center = new Vector3(a.x, 0f, a.z) + along * t;
                    float bed = float.MaxValue;
                    for (float w = -half; w <= half; w += spacing)
                        bed = Mathf.Min(bed, terrain.SampleHeight(center + across * w) + terrainY);
                    float surface = bed + depth;

                    for (float w = -half; w <= half; w += spacing)
                    {
                        Vector3 column = center + across * w;
                        float floor = terrain.SampleHeight(column) + terrainY + spacing * 0.5f;
                        for (float y = floor; y < surface; y += spacing)
                        {
                            var point = new Vector3(column.x, y, column.z);
                            if (Physics.CheckSphere(point, clearance, obstacleMask, QueryTriggerInteraction.Ignore) && !OnlyTerrain(point, clearance))
                                continue;
                            positions.Add(point);
                            velocities.Add(along * speed);
                        }
                    }
                }
            }
        }

        // The terrain's own collider overlaps the bottom row; only other colliders block a point.
        static bool OnlyTerrain(Vector3 point, float radius)
        {
            foreach (Collider c in Physics.OverlapSphere(point, radius, ~0, QueryTriggerInteraction.Ignore))
            {
                if (!(c is TerrainCollider))
                    return false;
            }
            return true;
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(0.2f, 0.6f, 1f);
            for (int i = 0; i + 1 < centerline.Length; i++)
                Gizmos.DrawLine(centerline[i], centerline[i + 1]);
        }
    }
}
