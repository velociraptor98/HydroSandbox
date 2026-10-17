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
        [SerializeField, Range(1, 48)] int blurMaxPixels = 24;

        [Header("Optics")]
        [Tooltip("Beer-Lambert absorption per meter (RGB). Real water is about (0.45, 0.07, 0.03); larger reads more tinted.")]
        [SerializeField] Vector3 absorption = new Vector3(1.6f, 0.32f, 0.22f);
        [SerializeField] Color scatterColor = new Color(0.06f, 0.22f, 0.24f);
        [SerializeField, Range(0f, 0.2f)] float refractionStrength = 0.035f;
        [SerializeField, Range(0f, 1f)] float smoothness = 0.95f;
        [Tooltip("Opacity per meter of thickness at the thin edges of the water.")]
        [SerializeField, Min(0f)] float edgeOpacity = 60f;

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

        [Header("Foam")]
        [SerializeField] Texture2D foamMask;
        [SerializeField] Texture2D foamColor;
        [SerializeField] Color foamTint = new Color(0.95f, 0.97f, 1f);
        [Tooltip("Foam texture repeats per meter.")]
        [SerializeField, Min(0.01f)] float foamScale = 3f;
        [Tooltip("Foam coverage per meter of aerated water at the surface.")]
        [SerializeField, Min(0f)] float foamAmount = 40f;
        [Tooltip("Whitening per meter of aerated water anywhere along the view ray (submerged bubble plumes).")]
        [SerializeField, Min(0f)] float bubbleAmount = 12f;
        [Tooltip("How deep below the visible surface aerated water still shows as foam, in particle radii.")]
        [SerializeField, Range(0.5f, 10f)] float foamDepth = 3f;

        Material material;

        public FluidSolver Solver => solver;
        public int BlurIterations => blurIterations;
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
            properties.SetFloat(Ids.FoamDepthFalloff, foamDepth * solver.ParticleRadius);
        }

        public void SetBlurProperties(MaterialPropertyBlock properties)
        {
            properties.SetFloat(Ids.BlurWorldRadius, blurRadius * solver.ParticleRadius);
            properties.SetFloat(Ids.BlurDepthFalloff, blurDepthFalloff * solver.ParticleRadius);
            properties.SetInt(Ids.BlurMaxRadius, blurMaxPixels);
        }

        public void SetCompositeProperties(MaterialPropertyBlock properties)
        {
            properties.SetVector(Ids.AbsorptionCoefficients, absorption);
            properties.SetColor(Ids.ScatterColor, scatterColor);
            properties.SetFloat(Ids.RefractionStrength, refractionStrength);
            properties.SetFloat(Ids.Smoothness, smoothness);
            properties.SetFloat(Ids.EdgeOpacity, edgeOpacity);
            properties.SetTexture(Ids.RippleNormal, rippleNormal != null ? rippleNormal : Texture2D.normalTexture);
            properties.SetFloat(Ids.RippleStrength, rippleNormal != null ? rippleStrength : 0f);
            properties.SetFloat(Ids.RippleScale, rippleScale);
            properties.SetFloat(Ids.RippleSpeed, rippleSpeed);
            properties.SetTexture(Ids.FoamMask, foamMask != null ? foamMask : Texture2D.whiteTexture);
            properties.SetTexture(Ids.FoamColor, foamColor != null ? foamColor : Texture2D.whiteTexture);
            properties.SetColor(Ids.FoamTint, foamTint);
            properties.SetFloat(Ids.FoamScale, foamScale);
            properties.SetFloat(Ids.FoamAmount, foamAmount);
            properties.SetFloat(Ids.BubbleAmount, bubbleAmount);
            properties.SetFloat(Ids.FlowSpeedMin, flowSpeedMin);
            properties.SetFloat(Ids.FlowSpeedRange, flowSpeedRange);
            properties.SetFloat(Ids.FlowSheen, flowSheen);
        }

        static class Ids
        {
            public static readonly int Positions = Shader.PropertyToID("_Positions");
            public static readonly int Velocities = Shader.PropertyToID("_Velocities");
            public static readonly int ParticleRadius = Shader.PropertyToID("_ParticleRadius");
            public static readonly int DepthRadiusScale = Shader.PropertyToID("_DepthRadiusScale");
            public static readonly int ThicknessScale = Shader.PropertyToID("_ThicknessScale");
            public static readonly int FoamDepthFalloff = Shader.PropertyToID("_FoamDepthFalloff");
            public static readonly int BlurWorldRadius = Shader.PropertyToID("_BlurWorldRadius");
            public static readonly int BlurDepthFalloff = Shader.PropertyToID("_BlurDepthFalloff");
            public static readonly int BlurMaxRadius = Shader.PropertyToID("_BlurMaxRadius");
            public static readonly int AbsorptionCoefficients = Shader.PropertyToID("_AbsorptionCoefficients");
            public static readonly int ScatterColor = Shader.PropertyToID("_ScatterColor");
            public static readonly int RefractionStrength = Shader.PropertyToID("_RefractionStrength");
            public static readonly int Smoothness = Shader.PropertyToID("_Smoothness");
            public static readonly int EdgeOpacity = Shader.PropertyToID("_EdgeOpacity");
            public static readonly int RippleNormal = Shader.PropertyToID("_RippleNormal");
            public static readonly int RippleStrength = Shader.PropertyToID("_RippleStrength");
            public static readonly int RippleScale = Shader.PropertyToID("_RippleScale");
            public static readonly int RippleSpeed = Shader.PropertyToID("_RippleSpeed");
            public static readonly int FoamMask = Shader.PropertyToID("_FoamMask");
            public static readonly int FoamColor = Shader.PropertyToID("_FoamColor");
            public static readonly int FoamTint = Shader.PropertyToID("_FoamTint");
            public static readonly int FoamScale = Shader.PropertyToID("_FoamScale");
            public static readonly int FoamAmount = Shader.PropertyToID("_FoamAmount");
            public static readonly int BubbleAmount = Shader.PropertyToID("_BubbleAmount");
            public static readonly int FlowSpeedMin = Shader.PropertyToID("_FlowSpeedMin");
            public static readonly int FlowSpeedRange = Shader.PropertyToID("_FlowSpeedRange");
            public static readonly int FlowSheen = Shader.PropertyToID("_FlowSheen");
        }
    }
}
