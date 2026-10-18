using System;
using UnityEngine;

namespace HydroSandbox
{
    [Serializable]
    public class FluidLiquid
    {
        public string name = "Water";
        [Tooltip("Swatch for UI only; the rendered look comes from absorption and scattering.")]
        public Color swatch = new Color(0.6f, 0.8f, 1f);
        [Tooltip("kg/m^3. Only the contrast with neighboring liquids matters: heavier liquid sinks through lighter.")]
        [Range(500f, 2000f)] public float density = 1000f;
        [Tooltip("XSPH velocity blend per substep, as the solver's viscosity setting.")]
        [Range(0f, 1f)] public float viscosity = 0.05f;
        [Tooltip("Fraction of tangential slip removed at the container walls; makes thick liquids cling.")]
        [Range(0f, 1f)] public float wallFriction;
        [Tooltip("Beer-Lambert absorption per meter (RGB).")]
        public Vector3 absorption = new Vector3(0.45f, 0.07f, 0.03f);
        [Tooltip("Scattering per meter (RGB): makes the liquid cloudy and lit in that color.")]
        public Vector3 scattering = new Vector3(0.02f, 0.03f, 0.04f);
    }

    [Serializable]
    public class FluidPairing
    {
        [Range(0, FluidMixing.MaxLiquids - 1)] public int a;
        [Range(0, FluidMixing.MaxLiquids - 1)] public int b = 1;
        public bool miscible = true;
        [Tooltip("Miscible only: rate (1/s) at which neighboring particles exchange composition.")]
        [Min(0f)] public float diffusion = 2f;
        [Tooltip("Repulsion between the two liquids at their interface (m/s^2). Acts as interfacial tension: immiscible liquids separate and bead up; for a miscible pair it keeps a blob or stream together until it has dissolved.")]
        [Min(0f)] public float tension = 20f;
    }

    /// <summary>
    /// Turns the solver (on the same GameObject) into a multi-liquid solver. Every particle carries
    /// a composition: the fraction of each of up to four liquids it holds, which sources set and
    /// mixing changes. Density, viscosity, wall friction and optics are blended by composition.
    /// Each pair of liquids is either miscible (compositions diffuse into each other) or
    /// immiscible (compositions never exchange); either kind can have interfacial tension.
    /// </summary>
    [RequireComponent(typeof(FluidSolver))]
    public class FluidMixing : MonoBehaviour
    {
        public const int MaxLiquids = 4;

        [SerializeField] FluidLiquid[] liquids = { new FluidLiquid() };
        [Tooltip("Liquid pairs without an entry are immiscible with the default tension.")]
        [SerializeField] FluidPairing[] pairings = Array.Empty<FluidPairing>();
        [SerializeField, Min(0f)] float defaultTension = 20f;
        [Tooltip("Multiplies the buoyancy from density contrast. PBF water is far more viscous than real water (XSPH and the density solve both damp relative motion), so true buoyancy moves droplets too slowly to see; this restores plausible rise and sink speeds.")]
        [SerializeField, Range(1f, 30f)] float buoyancyScale = 8f;

        public float BuoyancyScale
        {
            get => buoyancyScale;
            set => buoyancyScale = Mathf.Max(1f, value);
        }

        public FluidLiquid[] Liquids => liquids;
        public FluidPairing[] Pairings => pairings;
        public int LiquidCount => Mathf.Min(liquids.Length, MaxLiquids);

        public void Configure(FluidLiquid[] newLiquids, FluidPairing[] newPairings)
        {
            liquids = newLiquids;
            pairings = newPairings;
        }

        public FluidPairing FindPairing(int a, int b)
        {
            foreach (FluidPairing p in pairings)
            {
                if ((p.a == a && p.b == b) || (p.a == b && p.b == a))
                    return p;
            }
            return null;
        }

        FluidLiquid Liquid(int index) => index < liquids.Length ? liquids[index] : liquids[0];

        /// <summary>Per-liquid property in each component; liquids past the count copy liquid 0.</summary>
        Vector4 PerLiquid(Func<FluidLiquid, float> property) =>
            new Vector4(property(Liquid(0)), property(Liquid(1)), property(Liquid(2)), property(Liquid(3)));

        internal Vector4 Densities => PerLiquid(l => l.density);
        internal Vector4 Viscosities => PerLiquid(l => l.viscosity);
        internal Vector4 WallFrictions => PerLiquid(l => l.wallFriction);

        /// <summary>Symmetric pair matrices: diffusion rate (miscible pairs only) and interface repulsion.</summary>
        internal void BuildPairMatrices(out Matrix4x4 diffusion, out Matrix4x4 tension)
        {
            diffusion = Matrix4x4.zero;
            tension = Matrix4x4.zero;
            for (int a = 0; a < MaxLiquids; a++)
            for (int b = a + 1; b < MaxLiquids; b++)
            {
                FluidPairing p = FindPairing(a, b);
                float d = p != null && p.miscible ? p.diffusion : 0f;
                float t = p == null ? defaultTension : p.tension;
                diffusion[a, b] = diffusion[b, a] = d;
                tension[a, b] = tension[b, a] = t;
            }
        }

        internal void FillOptics(Vector4[] absorption, Vector4[] scattering)
        {
            for (int i = 0; i < MaxLiquids; i++)
            {
                absorption[i] = Liquid(i).absorption;
                scattering[i] = Liquid(i).scattering;
            }
        }
    }
}
