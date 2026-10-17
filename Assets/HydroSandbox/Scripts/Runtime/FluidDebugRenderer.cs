using UnityEngine;
using UnityEngine.Rendering;

namespace HydroSandbox
{
    /// <summary>Draws every solver particle as a sphere impostor straight from the GPU buffers.</summary>
    public class FluidDebugRenderer : MonoBehaviour
    {
        static readonly int PositionsId = Shader.PropertyToID("_Positions");
        static readonly int VelocitiesId = Shader.PropertyToID("_Velocities");
        static readonly int ParticleRadiusId = Shader.PropertyToID("_ParticleRadius");
        static readonly int SlowColorId = Shader.PropertyToID("_SlowColor");
        static readonly int FastColorId = Shader.PropertyToID("_FastColor");
        static readonly int FastSpeedId = Shader.PropertyToID("_FastSpeed");

        [SerializeField] FluidSolver solver;
        [SerializeField] Shader particleShader;
        [SerializeField] Color slowColor = new Color(0.08f, 0.32f, 0.85f);
        [SerializeField] Color fastColor = new Color(0.85f, 0.95f, 1f);
        [SerializeField, Min(0.01f)] float fastSpeed = 4f;

        Material material;
        MaterialPropertyBlock properties;

        void OnDisable()
        {
            if (material != null)
                Destroy(material);
            material = null;
        }

        void LateUpdate()
        {
            if (solver == null || particleShader == null || !solver.IsReady)
                return;

            if (material == null)
                material = new Material(particleShader) { hideFlags = HideFlags.DontSave };
            properties ??= new MaterialPropertyBlock();

            material.SetColor(SlowColorId, slowColor);
            material.SetColor(FastColorId, fastColor);
            material.SetFloat(FastSpeedId, fastSpeed);
            properties.SetBuffer(PositionsId, solver.PositionBuffer);
            properties.SetBuffer(VelocitiesId, solver.VelocityBuffer);
            properties.SetFloat(ParticleRadiusId, solver.ParticleRadius);

            Bounds bounds = solver.Container;
            bounds.Expand(solver.ParticleRadius * 4f);
            var renderParams = new RenderParams(material)
            {
                worldBounds = bounds,
                matProps = properties,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
                layer = gameObject.layer,
            };
            Graphics.RenderPrimitives(renderParams, MeshTopology.Triangles, 6, solver.ParticleCount);
        }
    }
}
