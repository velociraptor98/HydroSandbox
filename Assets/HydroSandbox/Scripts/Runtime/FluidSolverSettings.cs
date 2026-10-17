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
