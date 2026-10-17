using UnityEngine;
using UnityEngine.Rendering;

namespace HydroSandbox
{
    /// <summary>
    /// Secondary spray, foam and bubble particles for a <see cref="FluidSolver"/> on the same
    /// GameObject (Ihmsen et al. 2012). Emitted where water traps air or a wave crest breaks, then
    /// classified every frame by how much fluid surrounds them. FluidSurface renders them.
    /// </summary>
    [RequireComponent(typeof(FluidSolver))]
    public class FluidWhitewater : MonoBehaviour
    {
        const int ThreadGroupSize = 256;

        [SerializeField] ComputeShader whitewaterShader;
        [Tooltip("Ring buffer size. When full, the oldest whitewater particles are replaced.")]
        [SerializeField, Min(1024)] int capacity = 262144;

        [Header("Emission")]
        [Tooltip("Particles per second one fluid particle emits at full trapped-air potential (water colliding with water).")]
        [SerializeField, Min(0f)] float trappedAirRate = 600f;
        [Tooltip("Trapped-air potential over which emission ramps from none to full.")]
        [SerializeField] Vector2 trappedAirRange = new Vector2(2f, 15f);
        [Tooltip("Particles per second one fluid particle emits at full wave-crest potential (breaking crests).")]
        [SerializeField, Min(0f)] float waveCrestRate = 300f;
        [SerializeField] Vector2 waveCrestRange = new Vector2(0.5f, 4f);
        [Tooltip("Kinetic energy per unit mass (0.5 v^2, m^2/s^2) over which emission fades in, so slow water never foams.")]
        [SerializeField] Vector2 energyRange = new Vector2(0.5f, 5f);
        [SerializeField, Range(1, 32)] int maxEmitPerParticle = 8;
        [Tooltip("Random sideways speed (m/s) given to new particles.")]
        [SerializeField, Min(0f)] float emitSpread = 0.3f;
        [Tooltip("Surface-normal magnitude (in 1/h) above which a fluid particle counts as surface.")]
        [SerializeField, Range(0.05f, 1f)] float surfaceThreshold = 0.35f;

        [Header("Motion")]
        [Tooltip("Lifetime range in seconds. Foam ages at full rate, spray and bubbles at 30%.")]
        [SerializeField] Vector2 lifetimeRange = new Vector2(1.5f, 4f);
        [SerializeField, Min(0f)] float sprayDrag = 0.5f;
        [Tooltip("Bubble rise acceleration, in multiples of gravity.")]
        [SerializeField, Min(0f)] float bubbleBuoyancy = 2f;
        [Tooltip("Per-frame pull of bubble velocity toward the surrounding water's velocity.")]
        [SerializeField, Range(0f, 1f)] float bubbleDrag = 0.3f;
        [Tooltip("Fewer fluid neighbors than this: spray.")]
        [SerializeField, Min(0)] int sprayMaxNeighbors = 6;
        [Tooltip("More fluid neighbors than this: bubble. In between: foam.")]
        [SerializeField, Min(0)] int bubbleMinNeighbors = 20;

        GraphicsBuffer positions, velocities, state, surfaceNormals;
        int normalsKernel, emitKernel, advectKernel;
        int allocatedCapacity;

        public bool IsReady => positions != null;
        public int Capacity => allocatedCapacity;
        /// <summary>xyz = position, w = remaining life in seconds (dead at or below 0).</summary>
        public GraphicsBuffer PositionBuffer => positions;
        /// <summary>xyz = velocity, w = type (0 spray, 1 foam, 2 bubble).</summary>
        public GraphicsBuffer VelocityBuffer => velocities;

        void OnEnable()
        {
            if (whitewaterShader == null)
            {
                Debug.LogError("FluidWhitewater is missing its compute shader.", this);
                enabled = false;
                return;
            }
            normalsKernel = whitewaterShader.FindKernel("SurfaceNormals");
            emitKernel = whitewaterShader.FindKernel("EmitWhitewater");
            advectKernel = whitewaterShader.FindKernel("AdvectWhitewater");

            allocatedCapacity = capacity;
            positions = new GraphicsBuffer(GraphicsBuffer.Target.Structured, allocatedCapacity, 16);
            velocities = new GraphicsBuffer(GraphicsBuffer.Target.Structured, allocatedCapacity, 16);
            state = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 4, 4);
            Clear();
        }

        void OnDisable()
        {
            positions?.Release();
            velocities?.Release();
            state?.Release();
            surfaceNormals?.Release();
            positions = velocities = state = surfaceNormals = null;
        }

        /// <summary>Kills every whitewater particle, e.g. when the fluid is reset.</summary>
        public void Clear()
        {
            if (positions == null)
                return;
            positions.SetData(new Vector4[allocatedCapacity]);
            velocities.SetData(new Vector4[allocatedCapacity]);
            state.SetData(new uint[4]);
        }

        internal void Record(CommandBuffer cmd, FluidSolver solver, float frameDeltaTime, int frameIndex)
        {
            if (positions == null)
                return;
            int fluidCapacity = solver.Capacity;
            if (surfaceNormals == null || surfaceNormals.count != fluidCapacity)
            {
                surfaceNormals?.Release();
                surfaceNormals = new GraphicsBuffer(GraphicsBuffer.Target.Structured, fluidCapacity, 16);
            }

            var cs = whitewaterShader;
            solver.PushShaderParameters(cmd, cs, frameDeltaTime);
            cmd.SetComputeIntParam(cs, Ids.WhitewaterCapacity, allocatedCapacity);
            cmd.SetComputeFloatParam(cs, Ids.FrameDeltaTime, frameDeltaTime);
            cmd.SetComputeIntParam(cs, Ids.FrameSeed, frameIndex);
            cmd.SetComputeFloatParam(cs, Ids.ParticleRadius, solver.ParticleRadius);
            cmd.SetComputeFloatParam(cs, Ids.SurfaceThreshold, surfaceThreshold);
            cmd.SetComputeVectorParam(cs, Ids.TrappedAirRange, trappedAirRange);
            cmd.SetComputeVectorParam(cs, Ids.WaveCrestRange, waveCrestRange);
            cmd.SetComputeVectorParam(cs, Ids.EnergyRange, energyRange);
            cmd.SetComputeFloatParam(cs, Ids.TrappedAirRate, trappedAirRate);
            cmd.SetComputeFloatParam(cs, Ids.WaveCrestRate, waveCrestRate);
            cmd.SetComputeIntParam(cs, Ids.MaxEmitPerParticle, maxEmitPerParticle);
            cmd.SetComputeFloatParam(cs, Ids.EmitSpread, emitSpread);
            cmd.SetComputeVectorParam(cs, Ids.LifetimeRange, lifetimeRange);
            cmd.SetComputeFloatParam(cs, Ids.SprayDrag, sprayDrag);
            cmd.SetComputeFloatParam(cs, Ids.BubbleBuoyancy, bubbleBuoyancy);
            cmd.SetComputeFloatParam(cs, Ids.BubbleDrag, bubbleDrag);
            cmd.SetComputeIntParam(cs, Ids.SprayMaxNeighbors, sprayMaxNeighbors);
            cmd.SetComputeIntParam(cs, Ids.BubbleMinNeighbors, bubbleMinNeighbors);

            int fluidGroups = (fluidCapacity + ThreadGroupSize - 1) / ThreadGroupSize;

            BindFluid(cmd, solver, normalsKernel);
            cmd.SetComputeBufferParam(cs, normalsKernel, Ids.SurfaceNormals, surfaceNormals);
            cmd.DispatchCompute(cs, normalsKernel, fluidGroups, 1, 1);

            BindFluid(cmd, solver, emitKernel);
            cmd.SetComputeBufferParam(cs, emitKernel, Ids.SurfaceNormals, surfaceNormals);
            cmd.SetComputeBufferParam(cs, emitKernel, Ids.WhitewaterPositions, positions);
            cmd.SetComputeBufferParam(cs, emitKernel, Ids.WhitewaterVelocities, velocities);
            cmd.SetComputeBufferParam(cs, emitKernel, Ids.WhitewaterState, state);
            cmd.DispatchCompute(cs, emitKernel, fluidGroups, 1, 1);

            cmd.SetComputeBufferParam(cs, advectKernel, Ids.FluidState, solver.FluidStateBuffer);
            cmd.SetComputeBufferParam(cs, advectKernel, Ids.Positions, solver.PositionBuffer);
            cmd.SetComputeBufferParam(cs, advectKernel, Ids.Velocities, solver.VelocityBuffer);
            cmd.SetComputeBufferParam(cs, advectKernel, Ids.CellStart, solver.CellStartBuffer);
            cmd.SetComputeBufferParam(cs, advectKernel, Ids.CellCount, solver.CellCountBuffer);
            cmd.SetComputeBufferParam(cs, advectKernel, Ids.SortedCellId, solver.SortedCellIdBuffer);
            cmd.SetComputeBufferParam(cs, advectKernel, Ids.WhitewaterPositions, positions);
            cmd.SetComputeBufferParam(cs, advectKernel, Ids.WhitewaterVelocities, velocities);
            cmd.DispatchCompute(cs, advectKernel, (allocatedCapacity + ThreadGroupSize - 1) / ThreadGroupSize, 1, 1);
        }

        void BindFluid(CommandBuffer cmd, FluidSolver solver, int kernel)
        {
            var cs = whitewaterShader;
            cmd.SetComputeBufferParam(cs, kernel, Ids.FluidState, solver.FluidStateBuffer);
            cmd.SetComputeBufferParam(cs, kernel, Ids.Positions, solver.PositionBuffer);
            cmd.SetComputeBufferParam(cs, kernel, Ids.Velocities, solver.VelocityBuffer);
            cmd.SetComputeBufferParam(cs, kernel, Ids.NeighborList, solver.NeighborListBuffer);
            cmd.SetComputeBufferParam(cs, kernel, Ids.NeighborCount, solver.NeighborCountBuffer);
        }

        static class Ids
        {
            public static readonly int FluidState = Shader.PropertyToID("_FluidState");
            public static readonly int Positions = Shader.PropertyToID("_Positions");
            public static readonly int Velocities = Shader.PropertyToID("_Velocities");
            public static readonly int NeighborList = Shader.PropertyToID("_NeighborList");
            public static readonly int NeighborCount = Shader.PropertyToID("_NeighborCount");
            public static readonly int CellStart = Shader.PropertyToID("_CellStart");
            public static readonly int CellCount = Shader.PropertyToID("_CellCount");
            public static readonly int SortedCellId = Shader.PropertyToID("_SortedCellId");
            public static readonly int SurfaceNormals = Shader.PropertyToID("_SurfaceNormals");
            public static readonly int WhitewaterPositions = Shader.PropertyToID("_WhitewaterPositions");
            public static readonly int WhitewaterVelocities = Shader.PropertyToID("_WhitewaterVelocities");
            public static readonly int WhitewaterState = Shader.PropertyToID("_WhitewaterState");
            public static readonly int WhitewaterCapacity = Shader.PropertyToID("_WhitewaterCapacity");
            public static readonly int FrameDeltaTime = Shader.PropertyToID("_FrameDeltaTime");
            public static readonly int FrameSeed = Shader.PropertyToID("_FrameSeed");
            public static readonly int ParticleRadius = Shader.PropertyToID("_ParticleRadius");
            public static readonly int SurfaceThreshold = Shader.PropertyToID("_SurfaceThreshold");
            public static readonly int TrappedAirRange = Shader.PropertyToID("_TrappedAirRange");
            public static readonly int WaveCrestRange = Shader.PropertyToID("_WaveCrestRange");
            public static readonly int EnergyRange = Shader.PropertyToID("_EnergyRange");
            public static readonly int TrappedAirRate = Shader.PropertyToID("_TrappedAirRate");
            public static readonly int WaveCrestRate = Shader.PropertyToID("_WaveCrestRate");
            public static readonly int MaxEmitPerParticle = Shader.PropertyToID("_MaxEmitPerParticle");
            public static readonly int EmitSpread = Shader.PropertyToID("_EmitSpread");
            public static readonly int LifetimeRange = Shader.PropertyToID("_LifetimeRange");
            public static readonly int SprayDrag = Shader.PropertyToID("_SprayDrag");
            public static readonly int BubbleBuoyancy = Shader.PropertyToID("_BubbleBuoyancy");
            public static readonly int BubbleDrag = Shader.PropertyToID("_BubbleDrag");
            public static readonly int SprayMaxNeighbors = Shader.PropertyToID("_SprayMaxNeighbors");
            public static readonly int BubbleMinNeighbors = Shader.PropertyToID("_BubbleMinNeighbors");
        }
    }
}
