using System.Collections.Generic;
using UnityEngine;

namespace HydroSandbox
{
    /// <summary>
    /// Look settings for the screen-space water surface of one solver. FluidSurfaceFeature (added
    /// to the URP renderer) draws every active FluidSurface.
    /// </summary>
    public class FluidSurface : MonoBehaviour
    {
        public static readonly List<FluidSurface> Active = new List<FluidSurface>();

        [SerializeField] FluidSolver solver;
        [Tooltip("Optional spray/foam/bubble particles drawn over the water.")]
        [SerializeField] FluidWhitewater whitewater;
        [SerializeField] Shader surfaceShader;

        [Header("Surface Reconstruction")]
        [Tooltip("Sphere radius in the depth pass, as a multiple of particle radius. Above 1 closes the gaps between particles.")]
        [SerializeField, Range(1f, 2f)] float depthRadiusScale = 1.3f;
        [Tooltip("Corrects summed sphere chords toward true water depth (spheres at 2r spacing fill about half the volume).")]
        [SerializeField, Range(0.5f, 3f)] float thicknessScale = 1.9f;
        [Tooltip("Bilateral blur radius, as a multiple of particle radius.")]
        [SerializeField, Range(0f, 12f)] float blurRadius = 6f;
        [Tooltip("Horizontal + vertical blur passes. More iterations smooth the particle lattice out of the surface.")]
        [SerializeField, Range(1, 4)] int blurIterations = 2;
        [Tooltip("Depth difference, as a multiple of particle radius, beyond which the blur stops mixing.")]
        [SerializeField, Range(0.5f, 8f)] float blurDepthFalloff = 2.5f;
        [Tooltip("Blur taps per side. Beyond this many pixels of radius the taps spread out rather than the filter shrinking.")]
        [SerializeField, Range(1, 48)] int blurMaxPixels = 24;

        [Header("Optics")]
        [Tooltip("Beer-Lambert absorption per meter (RGB). Real water is about (0.45, 0.07, 0.03); larger reads more tinted.")]
        [SerializeField] Vector3 absorption = new Vector3(1.6f, 0.32f, 0.22f);
        [SerializeField] Color scatterColor = new Color(0.06f, 0.22f, 0.24f);
        [SerializeField, Range(0f, 0.2f)] float refractionStrength = 0.035f;
        [SerializeField, Range(0f, 1f)] float smoothness = 0.95f;
        [Tooltip("Opacity per meter of thickness at the thin edges of the water.")]
        [SerializeField, Min(0f)] float edgeOpacity = 60f;
        [Tooltip("Mixed liquids only: color splat radius as a multiple of particle radius. Wider splats smooth the particle grain out of colored liquid at the cost of sharp interfaces.")]
        [SerializeField, Range(1f, 4f)] float opticsRadiusScale = 2.2f;

        [Header("Ripples")]
        [SerializeField] Texture2D rippleNormal;
        [SerializeField, Range(0f, 2f)] float rippleStrength = 0.35f;
        [Tooltip("Ripple texture repeats per meter.")]
        [SerializeField, Min(0.01f)] float rippleScale = 1.6f;
        [SerializeField, Min(0f)] float rippleSpeed = 0.04f;

        [Header("Moving Water")]
        [Tooltip("Speed (m/s) at which water starts to look disturbed: rougher ripples and a silvery sheen.")]
        [SerializeField, Min(0f)] float flowSpeedMin = 0.8f;
        [SerializeField, Min(0.01f)] float flowSpeedRange = 2f;
        [SerializeField, Range(0f, 1f)] float flowSheen = 0.35f;

        [Header("Whitewater")]
        [Tooltip("Fluid particles with at most this many neighbors are loose droplets, drawn as spray instead of as surface.")]
        [SerializeField, Range(0, 8)] int dropletMaxNeighbors = 3;
        [Tooltip("Spray splat radius of a loose droplet, as a multiple of fluid particle radius.")]
        [SerializeField, Range(0.1f, 2f)] float dropletRadius = 0.7f;
        [Tooltip("Splat radius of one whitewater particle, as a multiple of fluid particle radius.")]
        [SerializeField, Range(0.05f, 2f)] float whitewaterRadius = 0.4f;
        [Tooltip("Splats never shrink below this many pixels; smaller ones are dimmed instead.")]
        [SerializeField, Range(0.5f, 4f)] float whitewaterMinPixels = 1.2f;
        [SerializeField, Min(0f)] float sprayIntensity = 0.5f;
        [SerializeField, Min(0f)] float foamIntensity = 0.45f;
        [SerializeField, Min(0f)] float bubbleIntensity = 0.3f;
        [Tooltip("How quickly submerged bubbles fade with depth below the surface, per meter.")]
        [SerializeField, Min(0f)] float bubbleAttenuation = 6f;
        [Tooltip("Photographed foam lace that breaks surface foam into strands.")]
        [SerializeField] Texture2D foamMask;
        [Tooltip("Foam lace repeats per meter.")]
        [SerializeField, Min(0.01f)] float foamScale = 3f;
        [SerializeField] Color foamTint = new Color(0.95f, 0.97f, 1f);

        Material material;
        readonly Vector4[] liquidAbsorption = new Vector4[FluidMixing.MaxLiquids];
        readonly Vector4[] liquidScattering = new Vector4[FluidMixing.MaxLiquids];
        static readonly Vector3[] AmbientDirections = { Vector3.up, Vector3.right, Vector3.down };
        readonly Color[] ambientColors = new Color[3];

        public FluidSolver Solver => solver;
        public FluidWhitewater Whitewater => whitewater != null && whitewater.isActiveAndEnabled && whitewater.IsReady ? whitewater : null;
        public int BlurIterations => blurIterations;
        /// <summary>True when the solver mixes several liquids, which adds the optics pass.</summary>
        public bool HasMixedLiquids => solver != null && solver.Mixing != null;
        public bool IsRenderable => isActiveAndEnabled && solver != null && solver.IsReady && surfaceShader != null;

        public Material Material
        {
            get
            {
                if (material == null && surfaceShader != null)
                    material = new Material(surfaceShader) { hideFlags = HideFlags.DontSave };
                return material;
            }
        }

        void OnEnable() => Active.Add(this);

        void OnDisable()
        {
            Active.Remove(this);
            if (material != null)
                DestroyImmediate(material);
            material = null;
        }

        /// <summary>Particle buffers and shape parameters for the particle passes.</summary>
        public void SetParticleProperties(MaterialPropertyBlock properties)
        {
            properties.SetBuffer(Ids.Positions, solver.PositionBuffer);
            properties.SetBuffer(Ids.Velocities, solver.VelocityBuffer);
            properties.SetFloat(Ids.ParticleRadius, solver.ParticleRadius);
            properties.SetFloat(Ids.DepthRadiusScale, depthRadiusScale);
            properties.SetFloat(Ids.ThicknessScale, thicknessScale);
            properties.SetBuffer(Ids.NeighborCount, solver.NeighborCountBuffer);
            properties.SetInt(Ids.DropletMaxNeighbors, dropletMaxNeighbors);
        }

        /// <summary>Particle-pass properties plus each liquid's optics, for the multi-liquid optics pass.</summary>
        public void SetOpticsProperties(MaterialPropertyBlock properties)
        {
            SetParticleProperties(properties);
            properties.SetBuffer(Ids.Composition, solver.CompositionBuffer);
            properties.SetFloat(Ids.OpticsRadiusScale, opticsRadiusScale);
            solver.Mixing.FillOptics(liquidAbsorption, liquidScattering);
            properties.SetVectorArray(Ids.LiquidAbsorption, liquidAbsorption);
            properties.SetVectorArray(Ids.LiquidScattering, liquidScattering);
        }

        public void SetBlurProperties(MaterialPropertyBlock properties)
        {
            properties.SetFloat(Ids.BlurWorldRadius, blurRadius * solver.ParticleRadius);
            properties.SetFloat(Ids.BlurDepthFalloff, blurDepthFalloff * solver.ParticleRadius);
            properties.SetInt(Ids.BlurMaxRadius, blurMaxPixels);
        }

        public void SetCompositeProperties(MaterialPropertyBlock properties)
        {
            SetAmbientProperties(properties);
            properties.SetVector(Ids.AbsorptionCoefficients, absorption);
            properties.SetColor(Ids.ScatterColor, scatterColor);
            properties.SetFloat(Ids.RefractionStrength, refractionStrength);
            properties.SetFloat(Ids.Smoothness, smoothness);
            properties.SetFloat(Ids.EdgeOpacity, edgeOpacity);
            properties.SetFloat(Ids.ParticleRadius, solver.ParticleRadius);
            properties.SetTexture(Ids.RippleNormal, rippleNormal != null ? rippleNormal : Texture2D.normalTexture);
            properties.SetFloat(Ids.RippleStrength, rippleNormal != null ? rippleStrength : 0f);
            properties.SetFloat(Ids.RippleScale, rippleScale);
            properties.SetFloat(Ids.RippleSpeed, rippleSpeed);
            properties.SetFloat(Ids.FlowSpeedMin, flowSpeedMin);
            properties.SetFloat(Ids.FlowSpeedRange, flowSpeedRange);
            properties.SetFloat(Ids.FlowSheen, flowSheen);
            properties.SetInt(Ids.MultiFluid, HasMixedLiquids ? 1 : 0);
        }

        /// <summary>
        /// Splat-pass properties. <paramref name="droplets"/> selects the loose fluid droplets
        /// instead of the whitewater particles; both draws share everything else.
        /// </summary>
        public void SetWhitewaterSplatProperties(MaterialPropertyBlock properties, bool droplets)
        {
            // Without whitewater the droplet draw still needs the whitewater buffers bound.
            bool hasWhitewater = Whitewater != null;
            properties.SetBuffer(Ids.WhitewaterPositions, hasWhitewater ? whitewater.PositionBuffer : solver.PositionBuffer);
            properties.SetBuffer(Ids.WhitewaterVelocities, hasWhitewater ? whitewater.VelocityBuffer : solver.VelocityBuffer);
            properties.SetBuffer(Ids.Positions, solver.PositionBuffer);
            properties.SetBuffer(Ids.NeighborCount, solver.NeighborCountBuffer);
            properties.SetInt(Ids.DropletMaxNeighbors, dropletMaxNeighbors);
            properties.SetInt(Ids.SplatSource, droplets ? 1 : 0);
            properties.SetFloat(Ids.DropletRadius, dropletRadius * solver.ParticleRadius);
            properties.SetFloat(Ids.WhitewaterRadius, whitewaterRadius * solver.ParticleRadius);
            properties.SetFloat(Ids.WhitewaterMinPixels, whitewaterMinPixels);
            properties.SetFloat(Ids.SprayIntensity, sprayIntensity);
            properties.SetFloat(Ids.FoamIntensity, foamIntensity);
            properties.SetFloat(Ids.BubbleIntensity, bubbleIntensity);
            properties.SetFloat(Ids.BubbleAttenuation, bubbleAttenuation);
            properties.SetTexture(Ids.FoamMask, foamMask != null ? foamMask : Texture2D.whiteTexture);
            properties.SetFloat(Ids.FoamScale, foamScale);
        }

        public void SetWhitewaterCompositeProperties(MaterialPropertyBlock properties)
        {
            SetAmbientProperties(properties);
            properties.SetColor(Ids.FoamTint, foamTint);
            properties.SetColor(Ids.ScatterColor, scatterColor);
        }

        void SetAmbientProperties(MaterialPropertyBlock properties)
        {
            RenderSettings.ambientProbe.Evaluate(AmbientDirections, ambientColors);
            properties.SetColor(Ids.AmbientUp, ambientColors[0] * RenderSettings.ambientIntensity);
            properties.SetColor(Ids.AmbientHorizon, ambientColors[1] * RenderSettings.ambientIntensity);
            properties.SetColor(Ids.AmbientDown, ambientColors[2] * RenderSettings.ambientIntensity);
        }

        static class Ids
        {
            public static readonly int Positions = Shader.PropertyToID("_Positions");
            public static readonly int Velocities = Shader.PropertyToID("_Velocities");
            public static readonly int Composition = Shader.PropertyToID("_Composition");
            public static readonly int MultiFluid = Shader.PropertyToID("_MultiFluid");
            public static readonly int OpticsRadiusScale = Shader.PropertyToID("_OpticsRadiusScale");
            public static readonly int LiquidAbsorption = Shader.PropertyToID("_LiquidAbsorption");
            public static readonly int LiquidScattering = Shader.PropertyToID("_LiquidScattering");
            public static readonly int ParticleRadius = Shader.PropertyToID("_ParticleRadius");
            public static readonly int DepthRadiusScale = Shader.PropertyToID("_DepthRadiusScale");
            public static readonly int ThicknessScale = Shader.PropertyToID("_ThicknessScale");
            public static readonly int NeighborCount = Shader.PropertyToID("_NeighborCount");
            public static readonly int DropletMaxNeighbors = Shader.PropertyToID("_DropletMaxNeighbors");
            public static readonly int SplatSource = Shader.PropertyToID("_SplatSource");
            public static readonly int DropletRadius = Shader.PropertyToID("_DropletRadius");
            public static readonly int BlurWorldRadius = Shader.PropertyToID("_BlurWorldRadius");
            public static readonly int BlurDepthFalloff = Shader.PropertyToID("_BlurDepthFalloff");
            public static readonly int BlurMaxRadius = Shader.PropertyToID("_BlurMaxRadius");
            public static readonly int AbsorptionCoefficients = Shader.PropertyToID("_AbsorptionCoefficients");
            public static readonly int AmbientUp = Shader.PropertyToID("_AmbientUp");
            public static readonly int AmbientHorizon = Shader.PropertyToID("_AmbientHorizon");
            public static readonly int AmbientDown = Shader.PropertyToID("_AmbientDown");
            public static readonly int ScatterColor = Shader.PropertyToID("_ScatterColor");
            public static readonly int RefractionStrength = Shader.PropertyToID("_RefractionStrength");
            public static readonly int Smoothness = Shader.PropertyToID("_Smoothness");
            public static readonly int EdgeOpacity = Shader.PropertyToID("_EdgeOpacity");
            public static readonly int RippleNormal = Shader.PropertyToID("_RippleNormal");
            public static readonly int RippleStrength = Shader.PropertyToID("_RippleStrength");
            public static readonly int RippleScale = Shader.PropertyToID("_RippleScale");
            public static readonly int RippleSpeed = Shader.PropertyToID("_RippleSpeed");
            public static readonly int FoamMask = Shader.PropertyToID("_FoamMask");
            public static readonly int FoamTint = Shader.PropertyToID("_FoamTint");
            public static readonly int FoamScale = Shader.PropertyToID("_FoamScale");
            public static readonly int FlowSpeedMin = Shader.PropertyToID("_FlowSpeedMin");
            public static readonly int FlowSpeedRange = Shader.PropertyToID("_FlowSpeedRange");
            public static readonly int FlowSheen = Shader.PropertyToID("_FlowSheen");
            public static readonly int WhitewaterPositions = Shader.PropertyToID("_WhitewaterPositions");
            public static readonly int WhitewaterVelocities = Shader.PropertyToID("_WhitewaterVelocities");
            public static readonly int WhitewaterRadius = Shader.PropertyToID("_WhitewaterRadius");
            public static readonly int WhitewaterMinPixels = Shader.PropertyToID("_WhitewaterMinPixels");
            public static readonly int SprayIntensity = Shader.PropertyToID("_SprayIntensity");
            public static readonly int FoamIntensity = Shader.PropertyToID("_FoamIntensity");
            public static readonly int BubbleIntensity = Shader.PropertyToID("_BubbleIntensity");
            public static readonly int BubbleAttenuation = Shader.PropertyToID("_BubbleAttenuation");
        }
    }
}
