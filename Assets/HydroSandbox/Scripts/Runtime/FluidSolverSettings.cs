using System;
using UnityEngine;

namespace HydroSandbox
{
    [Serializable]
    public class FluidSolverSettings
    {
        [Range(1, 8)] public int substeps = 3;
        [Range(1, 8)] public int solverIterations = 3;
        [Range(0.005f, 0.1f)] public float particleRadius = 0.02f;

        [Tooltip("1000 rests particles on a cubic lattice at 2x radius spacing; higher values pack them tighter.")]
        [Range(500f, 2000f)] public float restDensity = 1000f;

        [Tooltip("XSPH viscosity: how far each velocity is pulled toward its neighborhood average per substep.")]
        [Range(0f, 1f)] public float viscosity = 0.05f;

        public Vector3 gravity = new Vector3(0f, -9.81f, 0f);

        [Tooltip("How strongly under-dense particles are pulled back toward rest density (0 = only compression is resisted). Keeps streams and sheets together.")]
        [Range(0f, 1f)] public float cohesion = 0f;

        [Header("Aeration (foam)")]
        [Tooltip("Velocity change in one substep, in m/s, above which water traps air.")]
        [Min(0f)] public float aerationImpactMin = 1.0f;
        [Tooltip("Range of velocity change over which aeration ramps from 0 to 1.")]
        [Min(0.01f)] public float aerationImpactRange = 1.0f;
        [Tooltip("Decay rate of aeration, per second.")]
        [Min(0f)] public float aerationDecay = 0.35f;
        [Tooltip("How fast aeration spreads to neighboring water, per substep (XSPH-style blend).")]
        [Range(0f, 1f)] public float aerationDiffusion = 0.25f;
        [Tooltip("Fraction of gravity cancelled at full aeration (bubble buoyancy). Above 1 makes aerated water rise.")]
        [Range(0f, 3f)] public float aerationBuoyancy = 1.6f;

        [Header("Advanced")]
        [Tooltip("Simulated seconds per rendered frame. Fixed, so stability does not depend on frame rate.")]
        [Range(0.004f, 0.034f)] public float timeStep = 1f / 60f;

        [Tooltip("Smoothing radius as a multiple of particle radius.")]
        [Range(2f, 6f)] public float smoothingRadiusScale = 4f;

        [Tooltip("Constraint softening (epsilon), in units of 1/h^2. Higher is softer and more stable.")]
        [Range(0.01f, 10f)] public float constraintRelaxation = 0.5f;

        [Tooltip("Artificial pressure (s_corr k) against particle clustering, in units of h^2.")]
        [Range(0f, 0.5f)] public float artificialPressure = 0.03f;

        [Range(1f, 100f)] public float maxSpeed = 30f;

        public float SmoothingRadius => particleRadius * smoothingRadiusScale;
        public float ParticleSpacing => particleRadius * 2f;
    }
}
