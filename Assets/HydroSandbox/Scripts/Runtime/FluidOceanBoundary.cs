using UnityEngine;
using UnityEngine.Rendering;

namespace HydroSandbox
{
    /// <summary>
    /// Couples a solver's container to the <see cref="OceanWaves"/> sea: the container's footprint
    /// becomes a zone of particle water cut out of the heightfield. In a band along its side
    /// walls the particles are steered toward the ocean's orbital motion, so the open sea's waves
    /// travel into the zone and break on whatever is inside, while the heightfield sinks across
    /// the same band and is cut away beyond it.
    /// </summary>
    [RequireComponent(typeof(FluidSolver))]
    public class FluidOceanBoundary : MonoBehaviour
    {
        [SerializeField] ComputeShader oceanShader;
        [SerializeField] OceanWaves ocean;
        [SerializeField] float seaLevel;
        [Tooltip("Width of the forcing band along the zone's sides, m. Wider follows the sea more faithfully but leaves less free water.")]
        [SerializeField, Min(0.1f)] float bandWidth = 4f;
        [Tooltip("Typical water depth across the band, m: sets how much stronger a shallow-water flow must be than the deep-sea one to carry the same waves.")]
        [SerializeField, Min(0.5f)] float bandDepth = 4f;
        [Tooltip("Velocity relaxation rate at the outer wall, 1/s.")]
        [SerializeField, Min(0f)] float relaxationRate = 8f;
        [Tooltip("Width of the band's outer part over which the particle water fades in, m; the heightfield keeps its full height there and sinks only further in.")]
        [SerializeField, Min(0f)] float fadeWidth = 1.5f;
        [Tooltip("How far the heightfield sinks across the band, m, so the particles cover it.")]
        [SerializeField, Min(0f)] float heightfieldDepression = 1.2f;
        [Tooltip("Wave amplitude at the zone's edge relative to the open sea: the swell eases down approaching the island, so the particle water, whose waves break on the shelf, meets a sea of similar height.")]
        [SerializeField, Range(0.1f, 1f)] float shelterScale = 0.6f;
        [Tooltip("Distance outside the zone over which the sea recovers its full height, m.")]
        [SerializeField, Min(0.1f)] float shelterDistance = 25f;
        [Tooltip("Seconds over which the forcing eases in, so the still start does not slam.")]
        [SerializeField, Min(0f)] float rampSeconds = 4f;

        FluidSolver solver;
        int kernel;
        float simulatedSinceEnable;

        void OnEnable()
        {
            solver = GetComponent<FluidSolver>();
            simulatedSinceEnable = 0f;
            if (oceanShader != null)
                kernel = oceanShader.FindKernel("ForceTowardOcean");
        }

        void OnDisable() => Shader.SetGlobalVector(Ids.ZoneParams, Vector4.zero);

        public void Configure(ComputeShader shader, OceanWaves waves, float level)
        {
            oceanShader = shader;
            ocean = waves;
            seaLevel = level;
        }

        void Update()
        {
            Bounds zone = solver.Container;
            Shader.SetGlobalVector(Ids.Zone, new Vector4(zone.min.x, zone.min.z, zone.max.x, zone.max.z));
            Shader.SetGlobalVector(Ids.ZoneParams, new Vector4(bandWidth, heightfieldDepression, 1f, Mathf.Min(fadeWidth, bandWidth)));
            Shader.SetGlobalVector(Ids.Shelter, new Vector4(shelterScale, shelterDistance, 0f, 0f));
            if (!solver.Paused)
                simulatedSinceEnable += solver.Settings.timeStep;
        }

        /// <summary>Records the forcing for one substep of length <paramref name="dt"/>.</summary>
        internal void Record(CommandBuffer cmd, FluidSolver fluid, float dt)
        {
            if (oceanShader == null || ocean == null || !ocean.IsReady || ocean.Velocity(0) == null)
                return;
            var cs = oceanShader;
            fluid.PushShaderParameters(cmd, cs, dt);
            Bounds zone = fluid.Container;
            cmd.SetComputeVectorParam(cs, Ids.Zone, new Vector4(zone.min.x, zone.min.z, zone.max.x, zone.max.z));
            cmd.SetComputeVectorParam(cs, Ids.ZoneParams, new Vector4(bandWidth, heightfieldDepression, 1f, Mathf.Min(fadeWidth, bandWidth)));
            cmd.SetComputeVectorParam(cs, Ids.Shelter, new Vector4(shelterScale, shelterDistance, 0f, 0f));
            Vector2 sizes = ocean.CascadeSizes;
            cmd.SetComputeVectorParam(cs, Ids.CascadeSizes, new Vector4(sizes.x, sizes.y, 1f / sizes.x, 1f / sizes.y));
            cmd.SetComputeFloatParam(cs, Ids.ParticleRadius, fluid.ParticleRadius);
            cmd.SetComputeFloatParam(cs, Ids.SeaLevel, seaLevel);
            cmd.SetComputeFloatParam(cs, Ids.RelaxationRate, relaxationRate);
            float k = ocean.PeakWavenumber;
            float omega = Mathf.Sqrt(9.81f * k);
            cmd.SetComputeFloatParam(cs, Ids.PeakWavenumber, k);
            cmd.SetComputeFloatParam(cs, Ids.ShallowGain, Mathf.Clamp(Mathf.Sqrt(9.81f / bandDepth) / omega, 1f, 3f));
            OceanTsunami tsunami = OceanTsunami.Active;
            cmd.SetComputeVectorParam(cs, Ids.Tsunami0, tsunami != null ? tsunami.Params0 : Vector4.zero);
            cmd.SetComputeVectorParam(cs, Ids.Tsunami1, tsunami != null ? tsunami.Params1 : Vector4.one);
            cmd.SetComputeVectorParam(cs, Ids.Tsunami2, tsunami != null ? tsunami.Params2 : Vector4.one);
            cmd.SetComputeFloatParam(cs, Ids.Ramp, rampSeconds > 0f ? Mathf.Clamp01(simulatedSinceEnable / rampSeconds) : 1f);
            for (int c = 0; c < OceanWaves.CascadeCount; c++)
            {
                cmd.SetComputeTextureParam(cs, kernel, Ids.Displacement[c], ocean.Displacement(c));
                cmd.SetComputeTextureParam(cs, kernel, Ids.Velocity[c], ocean.Velocity(c));
            }
            cmd.SetComputeBufferParam(cs, kernel, Ids.Positions, fluid.PositionBuffer);
            cmd.SetComputeBufferParam(cs, kernel, Ids.Velocities, fluid.VelocityBuffer);
            cmd.DispatchCompute(cs, kernel, (fluid.Capacity + 255) / 256, 1, 1);
        }

        static class Ids
        {
            public static readonly int Zone = Shader.PropertyToID("_OceanZone");
            public static readonly int ZoneParams = Shader.PropertyToID("_OceanZoneParams");
            public static readonly int Shelter = Shader.PropertyToID("_OceanZoneShelter");
            public static readonly int CascadeSizes = Shader.PropertyToID("_OceanCascadeSizes");
            public static readonly int ParticleRadius = Shader.PropertyToID("_ParticleRadius");
            public static readonly int SeaLevel = Shader.PropertyToID("_SeaLevel");
            public static readonly int RelaxationRate = Shader.PropertyToID("_RelaxationRate");
            public static readonly int PeakWavenumber = Shader.PropertyToID("_PeakWavenumber");
            public static readonly int ShallowGain = Shader.PropertyToID("_ShallowGain");
            public static readonly int Ramp = Shader.PropertyToID("_Ramp");
            public static readonly int Tsunami0 = Shader.PropertyToID("_Tsunami0");
            public static readonly int Tsunami1 = Shader.PropertyToID("_Tsunami1");
            public static readonly int Tsunami2 = Shader.PropertyToID("_Tsunami2");
            public static readonly int Positions = Shader.PropertyToID("_Positions");
            public static readonly int Velocities = Shader.PropertyToID("_Velocities");
            public static readonly int[] Displacement = { Shader.PropertyToID("_OceanDisplacement0"), Shader.PropertyToID("_OceanDisplacement1") };
            public static readonly int[] Velocity = { Shader.PropertyToID("_OceanVelocity0"), Shader.PropertyToID("_OceanVelocity1") };
        }
    }
}
