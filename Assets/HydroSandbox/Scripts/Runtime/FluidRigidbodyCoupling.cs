using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace HydroSandbox
{
    /// <summary>
    /// Two-way coupling between the fluid and Rigidbodies with a coupled <see cref="FluidCollider"/>.
    /// The bodies push the water through their colliders. The water pushes back through buoyancy
    /// and drag computed from the fluid sampled at points just outside each body's surface:
    /// the wet fraction of the samples carries Archimedes' force (rho g V), and the flow relative
    /// to each wet sample carries drag. Samples are read back asynchronously, so forces arrive a
    /// frame or two late; because they are smooth functions of the body's pose, that latency is
    /// harmless (summing contact impulses instead is far too stiff to survive it).
    /// Unity physics is stepped here, once per fluid frame by the solver's time step, so bodies and
    /// water share a clock even when the frame rate drops.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    [RequireComponent(typeof(FluidSolver))]
    public class FluidRigidbodyCoupling : MonoBehaviour
    {
        const int MaxSamples = 4096;

        /// <summary>Raised after each script-driven physics step, with the simulated time step.</summary>
        public static event System.Action<float> PhysicsStepped;

        [SerializeField] ComputeShader couplingShader;
        [SerializeField, Min(0f)] float waterDensity = 1000f;
        [Tooltip("Quadratic drag coefficient of the body surface.")]
        [SerializeField, Min(0f)] float dragCoefficient = 0.8f;
        [Tooltip("Linear drag per square meter of wetted surface, in N s/m^3; damps bobbing.")]
        [SerializeField, Min(0f)] float linearDrag = 60f;
        [Tooltip("Fluid density / rest density over which a sample goes from dry to fully wet.")]
        [SerializeField] Vector2 wetRange = new Vector2(0.2f, 0.65f);

        struct Sample
        {
            public Rigidbody body;
            public Vector3 localPoint;     // in the body's frame
            public float volumeShare;      // body volume carried by this sample (m^3)
            public float area;             // surface area it represents (m^2)
        }

        readonly List<Sample> samples = new List<Sample>();
        readonly List<Vector4> samplePoints = new List<Vector4>();
        readonly List<FluidCollider.SurfaceSample> scratch = new List<FluidCollider.SurfaceSample>();
        Sample[] latestSamples = System.Array.Empty<Sample>();
        Vector4[] latestResults = System.Array.Empty<Vector4>();
        Sample[] pendingSamples;
        GraphicsBuffer pointBuffer, resultBuffer;
        int kernel;
        FluidSolver solver;
        SimulationMode previousMode;
        bool recorded;

        void OnEnable()
        {
            solver = GetComponent<FluidSolver>();
            previousMode = Physics.simulationMode;
            Physics.simulationMode = SimulationMode.Script;
            if (couplingShader == null)
            {
                Debug.LogError("FluidRigidbodyCoupling is missing its compute shader.", this);
                enabled = false;
                return;
            }
            kernel = couplingShader.FindKernel("SampleFluid");
            pointBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, MaxSamples, 16);
            resultBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, MaxSamples, 16);
        }

        void OnDisable()
        {
            Physics.simulationMode = previousMode;
            pointBuffer?.Release();
            resultBuffer?.Release();
            pointBuffer = resultBuffer = null;
            latestResults = System.Array.Empty<Vector4>();
        }

        void Update()
        {
            if (solver == null || solver.Paused || !solver.IsReady)
                return;
            ApplyFluidForces();
            Physics.Simulate(solver.Settings.timeStep);
            PhysicsStepped?.Invoke(solver.Settings.timeStep);
        }

        void ApplyFluidForces()
        {
            Vector3 gravity = Physics.gravity;
            int n = Mathf.Min(latestSamples.Length, latestResults.Length);
            for (int i = 0; i < n; i++)
            {
                Sample s = latestSamples[i];
                if (s.body == null)
                    continue;
                Vector4 result = latestResults[i];
                float wet = Mathf.InverseLerp(wetRange.x, wetRange.y, result.w);
                if (wet <= 0f)
                    continue;

                Transform t = s.body.transform;
                Vector3 point = t.TransformPoint(s.localPoint);
                // Buoyancy acts on the displaced volume, so apply it halfway toward the center of
                // mass: tilting still produces a restoring torque without making the body spin.
                Vector3 buoyancyPoint = Vector3.Lerp(s.body.worldCenterOfMass, point, 0.5f);
                s.body.AddForceAtPosition(-gravity * (waterDensity * s.volumeShare * wet), buoyancyPoint, ForceMode.Force);

                Vector3 relative = (Vector3)result - s.body.GetPointVelocity(point);
                Vector3 drag = (0.5f * waterDensity * dragCoefficient * relative.magnitude + linearDrag) * s.area * wet * relative;
                s.body.AddForceAtPosition(drag, point, ForceMode.Force);
            }
        }

        /// <summary>Records the sampling pass for the current body poses into the solver's frame.</summary>
        internal void Record(CommandBuffer cmd, FluidSolver fluid)
        {
            recorded = false;
            if (pointBuffer == null)
                return;

            samples.Clear();
            samplePoints.Clear();
            float offset = fluid.ParticleRadius * 1.5f;
            foreach (var collider in FluidCollider.Active)
            {
                Rigidbody body = collider.CoupledBody;
                if (body == null)
                    continue;
                scratch.Clear();
                collider.GetSurfaceSamples(scratch);
                if (scratch.Count == 0 || samples.Count + scratch.Count > MaxSamples)
                    continue;
                Transform t = body.transform;
                foreach (var ss in scratch)
                {
                    Vector3 world = ss.position + ss.normal * offset;
                    samplePoints.Add(world);
                    samples.Add(new Sample
                    {
                        body = body,
                        localPoint = t.InverseTransformPoint(ss.position),
                        volumeShare = ss.volumeShare,
                        area = ss.area,
                    });
                }
            }
            if (samples.Count == 0)
                return;

            var cs = couplingShader;
            fluid.PushShaderParameters(cmd, cs, fluid.Settings.timeStep);
            cmd.SetBufferData(pointBuffer, samplePoints);
            cmd.SetComputeIntParam(cs, Ids.SampleCount, samples.Count);
            cmd.SetComputeBufferParam(cs, kernel, Ids.SamplePoints, pointBuffer);
            cmd.SetComputeBufferParam(cs, kernel, Ids.SampleResults, resultBuffer);
            cmd.SetComputeBufferParam(cs, kernel, Ids.Positions, fluid.PositionBuffer);
            cmd.SetComputeBufferParam(cs, kernel, Ids.Velocities, fluid.VelocityBuffer);
            cmd.SetComputeBufferParam(cs, kernel, Ids.CellStart, fluid.CellStartBuffer);
            cmd.SetComputeBufferParam(cs, kernel, Ids.CellCount, fluid.CellCountBuffer);
            cmd.SetComputeBufferParam(cs, kernel, Ids.SortedCellId, fluid.SortedCellIdBuffer);
            cmd.SetComputeBufferParam(cs, kernel, Ids.FluidState, fluid.FluidStateBuffer);
            cmd.DispatchCompute(cs, kernel, (samples.Count + 63) / 64, 1, 1);
            pendingSamples = samples.ToArray();
            recorded = true;
        }

        /// <summary>Requests the readback of the pass recorded this frame (call after executing it).</summary>
        internal void RequestResults()
        {
            if (!recorded)
                return;
            Sample[] snapshot = pendingSamples;
            AsyncGPUReadback.Request(resultBuffer, snapshot.Length * 16, 0, request =>
            {
                if (request.hasError || this == null || !isActiveAndEnabled)
                    return;
                latestResults = request.GetData<Vector4>().ToArray();
                latestSamples = snapshot;
            });
        }

        static class Ids
        {
            public static readonly int SampleCount = Shader.PropertyToID("_SampleCount");
            public static readonly int SamplePoints = Shader.PropertyToID("_SamplePoints");
            public static readonly int SampleResults = Shader.PropertyToID("_SampleResults");
            public static readonly int Positions = Shader.PropertyToID("_Positions");
            public static readonly int Velocities = Shader.PropertyToID("_Velocities");
            public static readonly int CellStart = Shader.PropertyToID("_CellStart");
            public static readonly int CellCount = Shader.PropertyToID("_CellCount");
            public static readonly int SortedCellId = Shader.PropertyToID("_SortedCellId");
            public static readonly int FluidState = Shader.PropertyToID("_FluidState");
        }
    }
}
