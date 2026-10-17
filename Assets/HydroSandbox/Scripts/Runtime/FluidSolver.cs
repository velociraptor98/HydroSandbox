using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.Serialization;

namespace HydroSandbox
{
    /// <summary>
    /// GPU Position-Based Fluids solver. Owns a fixed-capacity particle pool and records every
    /// solver stage, plus all active <see cref="FluidSource"/>s and <see cref="FluidSink"/>s, into one
    /// command buffer per frame.
    /// </summary>
    public class FluidSolver : MonoBehaviour
    {
        const int ThreadGroupSize = 256;
        const int ScanBlockSize = 1024;
        const int MaxNeighbors = 64;
        const int MaxSinks = 32;
        const int MaxSpawnLayers = 256;
        const int MaxSpawnPoints = 16384;
        const int AliveReadbackInterval = 10;
        const float ReferenceDensity = 1000f;

        [Header("Compute Shaders")]
        [SerializeField] ComputeShader predictShader;
        [SerializeField] ComputeShader neighborSearchShader;
        [SerializeField] ComputeShader neighborListShader;
        [SerializeField] ComputeShader densityConstraintShader;
        [SerializeField] ComputeShader collisionShader;
        [SerializeField] ComputeShader velocityShader;
        [SerializeField] ComputeShader emissionShader;

        [Header("Simulation")]
        [SerializeField] FluidSolverSettings settings = new FluidSolverSettings();
        [FormerlySerializedAs("particleCount")]
        [Tooltip("Pool size. Sources stop emitting while the pool is full.")]
        [SerializeField, Min(1)] int capacity = 100000;

        [Tooltip("Particles placed in the spawn region at reset; the rest of the pool starts empty.")]
        [SerializeField, Min(0)] int initialParticles = 100000;
        [SerializeField] bool paused;

        [Header("Scene")]
        [Tooltip("World-space box the particles are confined to.")]
        [SerializeField] Bounds container = new Bounds(new Vector3(0f, 1.5f, 0f), new Vector3(4f, 3f, 2f));

        [Tooltip("Fraction of the container the initial block covers. The block stacks upward until every particle is placed.")]
        [SerializeField] Vector3 spawnMin = Vector3.zero;
        [SerializeField] Vector3 spawnMax = new Vector3(0.4f, 1f, 1f);

        GraphicsBuffer positions, positionsAlt;
        GraphicsBuffer predicted, predictedAlt;
        GraphicsBuffer velocities, velocitiesAlt;
        GraphicsBuffer fluidType, fluidTypeAlt;
        GraphicsBuffer density, lambda, deltaPosition;
        GraphicsBuffer particleCell, sortedCellId;
        GraphicsBuffer cellCount, cellStart;
        GraphicsBuffer neighborList, neighborCount;
        GraphicsBuffer fluidState;
        GraphicsBuffer sinkRecords, sinkCounters, spawnLayers, spawnPattern;
        readonly List<GraphicsBuffer> scanBlockSums = new List<GraphicsBuffer>();
        readonly List<GraphicsBuffer> scanBlockOffsets = new List<GraphicsBuffer>();

        int poolSize;
        int hashTableSize;
        int bufferGeneration;
        int frameIndex;
        readonly List<SinkRecord> sinkList = new List<SinkRecord>();
        readonly uint[] zeroCounters = new uint[MaxSinks];
        readonly List<SpawnLayer> layerList = new List<SpawnLayer>();
        readonly List<Vector2> patternList = new List<Vector2>();
        int gridBitsX, gridBitsY, gridBitsZ;

        CommandBuffer cmd;
        CustomSampler solveSampler;
        Recorder solveRecorder;

        int predictKernel;
        int clearCountsKernel, hashKernel, scanKernel, addOffsetsKernel, reorderKernel;
        int aliveCountKernel;
        int neighborListKernel;
        int sinkKernel, spawnKernel;
        int lambdaKernel, deltaKernel, applyDeltaKernel;
        int containerKernel;
        int updateVelocityKernel, viscosityKernel;

        float cachedMassSmoothing = -1f, cachedMassSpacing = -1f, cachedMass;

        public FluidSolverSettings Settings => settings;
        public bool IsReady => positions != null;
        public int Capacity => poolSize;

        /// <summary>Live particle count, read back asynchronously every few frames (display only).</summary>
        public int AliveCount { get; private set; }

        public float ParticleRadius => settings.particleRadius;
        public GraphicsBuffer PositionBuffer => positions;
        /// <summary>xyz = velocity, w = aeration (0..1).</summary>
        public GraphicsBuffer VelocityBuffer => velocities;
        public float GpuSolveMilliseconds { get; private set; }
        public bool HasGpuSolveTiming => SystemInfo.supportsGpuRecorder;

        public bool Paused
        {
            get => paused;
            set => paused = value;
        }

        public Bounds Container
        {
            get => container;
            set => container = value;
        }

        /// <summary>Resizes the pool and fills it completely at reset.</summary>
        public void SetParticleCount(int count)
        {
            capacity = Mathf.Max(1, count);
            initialParticles = capacity;
            ResetSimulation();
        }

        [ContextMenu("Reset Simulation")]
        public void ResetSimulation()
        {
            if (cmd == null)
                return;
            ReleaseBuffers();
            AllocateBuffers();
            UploadInitialState();
        }

        void OnEnable()
        {
            if (!SystemInfo.supportsComputeShaders)
            {
                Debug.LogError("FluidSolver requires compute shader support.", this);
                enabled = false;
                return;
            }
            if (predictShader == null || neighborSearchShader == null || neighborListShader == null ||
                densityConstraintShader == null || collisionShader == null || velocityShader == null ||
                emissionShader == null)
            {
                Debug.LogError("FluidSolver is missing compute shader references.", this);
                enabled = false;
                return;
            }

            FindKernels();
            cmd = new CommandBuffer { name = "HydroSandbox Fluid Solve" };
            solveSampler = CustomSampler.Create("HydroSandbox.FluidSolve", true);
            solveRecorder = solveSampler.GetRecorder();
            solveRecorder.enabled = true;
            ResetSimulation();
        }

        void OnDisable()
        {
            ReleaseBuffers();
            cmd?.Release();
            cmd = null;
            if (solveRecorder != null)
                solveRecorder.enabled = false;
        }

        void Update()
        {
            if (!IsReady)
                return;

            GpuSolveMilliseconds = solveRecorder.isValid ? solveRecorder.gpuElapsedNanoseconds * 1e-6f : 0f;
            if (paused)
                return;

            int substeps = Mathf.Max(1, settings.substeps);
            cmd.Clear();
            cmd.BeginSample(solveSampler);
            PushParameters(settings.timeStep / substeps);
            RecordEmission(settings.timeStep);
            for (int i = 0; i < substeps; i++)
                RecordSubstep();
            cmd.EndSample(solveSampler);
            Graphics.ExecuteCommandBuffer(cmd);

            if (++frameIndex % AliveReadbackInterval == 0)
                RequestAliveCount();
        }

        void RequestAliveCount()
        {
            int generation = bufferGeneration;
            AsyncGPUReadback.Request(fluidState, request =>
            {
                if (!request.hasError && generation == bufferGeneration)
                    AliveCount = (int)request.GetData<uint>()[0];
            });
        }

        void FindKernels()
        {
            predictKernel = predictShader.FindKernel("Predict");
            clearCountsKernel = neighborSearchShader.FindKernel("ClearCellCounts");
            hashKernel = neighborSearchShader.FindKernel("HashParticles");
            scanKernel = neighborSearchShader.FindKernel("ScanBlocks");
            addOffsetsKernel = neighborSearchShader.FindKernel("AddBlockOffsets");
            reorderKernel = neighborSearchShader.FindKernel("ReorderParticles");
            aliveCountKernel = neighborSearchShader.FindKernel("UpdateAliveCount");
            neighborListKernel = neighborListShader.FindKernel("BuildNeighborList");
            lambdaKernel = densityConstraintShader.FindKernel("ComputeLambda");
            deltaKernel = densityConstraintShader.FindKernel("ComputeDeltaPosition");
            applyDeltaKernel = densityConstraintShader.FindKernel("ApplyDeltaPosition");
            containerKernel = collisionShader.FindKernel("ResolveContainer");
            updateVelocityKernel = velocityShader.FindKernel("UpdateVelocity");
            viscosityKernel = velocityShader.FindKernel("ApplyViscosity");
            sinkKernel = emissionShader.FindKernel("ApplySinks");
            spawnKernel = emissionShader.FindKernel("SpawnParticles");
        }

        void AllocateBuffers()
        {
            poolSize = capacity;
            bufferGeneration++;
            // About two cells per particle keeps the wrap period larger than typical scenes.
            // Bits are split x >= z >= y because container scenes are usually shallowest in y.
            hashTableSize = Mathf.NextPowerOfTwo(Mathf.Max(poolSize * 2, 1 << 15));
            int totalBits = Mathf.RoundToInt(Mathf.Log(hashTableSize, 2));
            gridBitsX = (totalBits + 2) / 3;
            gridBitsZ = (totalBits - gridBitsX + 1) / 2;
            gridBitsY = totalBits - gridBitsX - gridBitsZ;

            positions = CreateBuffer(poolSize, 16);
            positionsAlt = CreateBuffer(poolSize, 16);
            predicted = CreateBuffer(poolSize, 16);
            predictedAlt = CreateBuffer(poolSize, 16);
            velocities = CreateBuffer(poolSize, 16);
            velocitiesAlt = CreateBuffer(poolSize, 16);
            fluidType = CreateBuffer(poolSize, 4);
            fluidTypeAlt = CreateBuffer(poolSize, 4);
            density = CreateBuffer(poolSize, 4);
            lambda = CreateBuffer(poolSize, 4);
            deltaPosition = CreateBuffer(poolSize, 16);
            particleCell = CreateBuffer(poolSize, 8);
            sortedCellId = CreateBuffer(poolSize, 4);
            // One extra bucket past the table collects dead particles.
            cellCount = CreateBuffer(hashTableSize + 1, 4);
            cellStart = CreateBuffer(hashTableSize + 1, 4);
            neighborList = CreateBuffer(poolSize * MaxNeighbors, 4);
            neighborCount = CreateBuffer(poolSize, 4);
            fluidState = CreateBuffer(4, 4);
            sinkRecords = CreateBuffer(MaxSinks, Marshal.SizeOf<SinkRecord>());
            sinkCounters = CreateBuffer(MaxSinks, 4);
            spawnLayers = CreateBuffer(MaxSpawnLayers, Marshal.SizeOf<SpawnLayer>());
            spawnPattern = CreateBuffer(MaxSpawnPoints, 8);

            int levelCount = hashTableSize + 1;
            do
            {
                int blocks = (levelCount + ScanBlockSize - 1) / ScanBlockSize;
                scanBlockSums.Add(CreateBuffer(blocks, 4));
                scanBlockOffsets.Add(CreateBuffer(blocks, 4));
                levelCount = blocks;
            } while (levelCount > 1);
        }

        static GraphicsBuffer CreateBuffer(int count, int stride) =>
            new GraphicsBuffer(GraphicsBuffer.Target.Structured, count, stride);

        void ReleaseBuffers()
        {
            foreach (var buffer in new[]
                     {
                         positions, positionsAlt, predicted, predictedAlt, velocities, velocitiesAlt,
                         fluidType, fluidTypeAlt, density, lambda, deltaPosition,
                         particleCell, sortedCellId, cellCount, cellStart, neighborList, neighborCount,
                         fluidState, sinkRecords, sinkCounters, spawnLayers, spawnPattern
                     })
                buffer?.Release();
            positions = positionsAlt = predicted = predictedAlt = velocities = velocitiesAlt = null;
            fluidType = fluidTypeAlt = density = lambda = deltaPosition = null;
            particleCell = sortedCellId = cellCount = cellStart = neighborList = neighborCount = null;
            fluidState = sinkRecords = sinkCounters = spawnLayers = spawnPattern = null;

            foreach (var buffer in scanBlockSums)
                buffer.Release();
            foreach (var buffer in scanBlockOffsets)
                buffer.Release();
            scanBlockSums.Clear();
            scanBlockOffsets.Clear();
            poolSize = 0;
            AliveCount = 0;
        }

        void UploadInitialState()
        {
            float r = settings.particleRadius;
            float spacing = settings.ParticleSpacing;
            Vector3 innerMin = container.min + Vector3.one * r;
            Vector3 innerMax = container.max - Vector3.one * r;
            Vector3 regionMin = innerMin + Vector3.Scale(innerMax - innerMin, spawnMin);
            Vector3 regionSize = Vector3.Scale(innerMax - innerMin, spawnMax - spawnMin);

            int nx = Mathf.Max(1, Mathf.FloorToInt(regionSize.x / spacing) + 1);
            int nz = Mathf.Max(1, Mathf.FloorToInt(regionSize.z / spacing) + 1);
            int initial = Mathf.Min(initialParticles, poolSize);
            int layers = (initial + nx * nz - 1) / (nx * nz);
            if (regionMin.y + (layers - 1) * spacing > innerMax.y)
                Debug.LogWarning($"FluidSolver: {initial} particles stack above the container; enlarge the container or spawn region.", this);

            // Tiny jitter breaks the perfect lattice symmetry that otherwise makes the column
            // collapse in unnaturally straight sheets.
            var random = new System.Random(1234);
            var data = new Vector4[poolSize];
            for (int i = 0; i < initial; i++)
            {
                int x = i % nx;
                int z = (i / nx) % nz;
                int y = i / (nx * nz);
                Vector3 jitter = new Vector3((float)random.NextDouble() - 0.5f, (float)random.NextDouble() - 0.5f,
                    (float)random.NextDouble() - 0.5f) * (0.02f * spacing);
                Vector3 p = regionMin + new Vector3(x, y, z) * spacing + jitter;
                data[i] = new Vector4(p.x, p.y, p.z, 1f);
            }
            positions.SetData(data);
            predicted.SetData(data);

            System.Array.Clear(data, 0, data.Length);
            velocities.SetData(data);
            fluidType.SetData(new uint[poolSize]);
            fluidState.SetData(new uint[] { (uint)initial, 0, 0, 0 });
            AliveCount = initial;
        }

        // Mass is chosen so a cubic lattice at 2r spacing evaluates to ReferenceDensity, which
        // makes restDensity directly control packing relative to that lattice.
        float ParticleMass(float h, float spacing)
        {
            if (Mathf.Approximately(h, cachedMassSmoothing) && Mathf.Approximately(spacing, cachedMassSpacing))
                return cachedMass;

            double h2 = (double)h * h;
            int n = Mathf.CeilToInt(h / spacing);
            double sum = 0;
            for (int x = -n; x <= n; x++)
            for (int y = -n; y <= n; y++)
            for (int z = -n; z <= n; z++)
            {
                double r2 = (x * x + y * y + z * z) * (double)spacing * spacing;
                if (r2 < h2)
                    sum += (h2 - r2) * (h2 - r2) * (h2 - r2);
            }
            sum *= 315.0 / (64.0 * Mathf.PI * System.Math.Pow(h, 9));

            cachedMassSmoothing = h;
            cachedMassSpacing = spacing;
            cachedMass = (float)(ReferenceDensity / sum);
            return cachedMass;
        }

        void PushParameters(float dt)
        {
            float r = settings.particleRadius;
            float h = settings.SmoothingRadius;
            float h2 = h * h;
            float poly6 = 315f / (64f * Mathf.PI * Mathf.Pow(h, 9));
            float deltaQ2 = 0.04f * h2;
            float wDeltaQ = poly6 * Mathf.Pow(h2 - deltaQ2, 3);
            Vector3 inset = Vector3.one * r;

            foreach (var cs in new[] { predictShader, neighborSearchShader, neighborListShader, densityConstraintShader, collisionShader, velocityShader, emissionShader })
            {
                cmd.SetComputeIntParam(cs, Ids.ParticleCount, poolSize);
                cmd.SetComputeFloatParam(cs, Ids.DeltaTime, dt);
                cmd.SetComputeVectorParam(cs, Ids.Gravity, settings.gravity);
                cmd.SetComputeFloatParam(cs, Ids.MaxSpeed, settings.maxSpeed);
                cmd.SetComputeFloatParam(cs, Ids.SmoothingRadius, h);
                cmd.SetComputeFloatParam(cs, Ids.SmoothingRadiusSq, h2);
                cmd.SetComputeFloatParam(cs, Ids.InvCellSize, 1f / h);
                cmd.SetComputeIntParam(cs, Ids.GridBitsX, gridBitsX);
                cmd.SetComputeIntParam(cs, Ids.GridBitsY, gridBitsY);
                cmd.SetComputeIntParam(cs, Ids.GridBitsZ, gridBitsZ);
                cmd.SetComputeIntParam(cs, Ids.HashTableSize, hashTableSize);
                cmd.SetComputeFloatParam(cs, Ids.ParticleMass, ParticleMass(h, settings.ParticleSpacing));
                cmd.SetComputeFloatParam(cs, Ids.InvRestDensity, 1f / settings.restDensity);
                cmd.SetComputeFloatParam(cs, Ids.Poly6Coeff, poly6);
                cmd.SetComputeFloatParam(cs, Ids.SpikyGradCoeff, -45f / (Mathf.PI * Mathf.Pow(h, 6)));
                cmd.SetComputeFloatParam(cs, Ids.ConstraintRelaxation, settings.constraintRelaxation / h2);
                cmd.SetComputeFloatParam(cs, Ids.Cohesion, settings.cohesion);
                cmd.SetComputeFloatParam(cs, Ids.SCorrK, settings.artificialPressure * h2);
                cmd.SetComputeFloatParam(cs, Ids.SCorrInvWdq, 1f / wDeltaQ);
                cmd.SetComputeFloatParam(cs, Ids.Viscosity, settings.viscosity);
                cmd.SetComputeFloatParam(cs, Ids.AerationImpactMin, settings.aerationImpactMin);
                cmd.SetComputeFloatParam(cs, Ids.AerationImpactRange, settings.aerationImpactRange);
                cmd.SetComputeFloatParam(cs, Ids.AerationDecay, settings.aerationDecay);
                cmd.SetComputeFloatParam(cs, Ids.AerationBuoyancy, settings.aerationBuoyancy);
                cmd.SetComputeFloatParam(cs, Ids.AerationDiffusion, settings.aerationDiffusion);
                cmd.SetComputeVectorParam(cs, Ids.ContainerMin, container.min + inset);
                cmd.SetComputeVectorParam(cs, Ids.ContainerMax, container.max - inset);
            }
        }

        void RecordEmission(float dt)
        {
            float spacing = settings.ParticleSpacing;
            var es = emissionShader;

            sinkList.Clear();
            foreach (var sink in FluidSink.Active)
            {
                if (sinkList.Count < MaxSinks)
                    sinkList.Add(sink.BuildRecord(dt, spacing));
            }
            if (sinkList.Count > 0)
            {
                cmd.SetBufferData(sinkRecords, sinkList);
                cmd.SetBufferData(sinkCounters, zeroCounters);
                cmd.SetComputeIntParam(es, Ids.SinkCount, sinkList.Count);
                cmd.SetComputeBufferParam(es, sinkKernel, Ids.Sinks, sinkRecords);
                cmd.SetComputeBufferParam(es, sinkKernel, Ids.SinkCounters, sinkCounters);
                cmd.SetComputeBufferParam(es, sinkKernel, Ids.FluidState, fluidState);
                cmd.SetComputeBufferParam(es, sinkKernel, Ids.Positions, positions);
                cmd.SetComputeBufferParam(es, sinkKernel, Ids.Predicted, predicted);
                cmd.SetComputeBufferParam(es, sinkKernel, Ids.Velocities, velocities);
                DispatchParticles(es, sinkKernel);
            }

            layerList.Clear();
            patternList.Clear();
            int pointCount = 0;
            foreach (var source in FluidSource.Active)
            {
                if (layerList.Count < MaxSpawnLayers)
                    source.Emit(dt, spacing, layerList, patternList, ref pointCount, MaxSpawnPoints);
            }
            if (layerList.Count > MaxSpawnLayers)
            {
                layerList.RemoveRange(MaxSpawnLayers, layerList.Count - MaxSpawnLayers);
                SpawnLayer last = layerList[MaxSpawnLayers - 1];
                pointCount = (int)(last.firstPoint + last.patternCount);
            }
            if (pointCount > 0)
            {
                cmd.SetBufferData(spawnLayers, layerList);
                cmd.SetBufferData(spawnPattern, patternList);
                cmd.SetComputeIntParam(es, Ids.SpawnLayerCount, layerList.Count);
                cmd.SetComputeIntParam(es, Ids.SpawnPointCount, pointCount);
                cmd.SetComputeFloatParam(es, Ids.SpawnJitter, 0.02f * spacing);
                cmd.SetComputeIntParam(es, Ids.FrameSeed, frameIndex * 104729);
                cmd.SetComputeBufferParam(es, spawnKernel, Ids.SpawnLayers, spawnLayers);
                cmd.SetComputeBufferParam(es, spawnKernel, Ids.SpawnPattern, spawnPattern);
                cmd.SetComputeBufferParam(es, spawnKernel, Ids.FluidState, fluidState);
                cmd.SetComputeBufferParam(es, spawnKernel, Ids.Positions, positions);
                cmd.SetComputeBufferParam(es, spawnKernel, Ids.Predicted, predicted);
                cmd.SetComputeBufferParam(es, spawnKernel, Ids.Velocities, velocities);
                cmd.SetComputeBufferParam(es, spawnKernel, Ids.FluidType, fluidType);
                cmd.DispatchCompute(es, spawnKernel, Groups(pointCount, ThreadGroupSize), 1, 1);
            }
        }

        void RecordSubstep()
        {
            var ps = predictShader;
            cmd.SetComputeBufferParam(ps, predictKernel, Ids.Positions, positions);
            cmd.SetComputeBufferParam(ps, predictKernel, Ids.Velocities, velocities);
            cmd.SetComputeBufferParam(ps, predictKernel, Ids.Predicted, predicted);
            cmd.SetComputeBufferParam(ps, predictKernel, Ids.Density, density);
            DispatchParticles(ps, predictKernel);

            // Also before the neighbor search: the neighbor list is fixed for the substep, so
            // particles predicted outside the box must be pulled in before it is built.
            cmd.SetComputeBufferParam(collisionShader, containerKernel, Ids.Predicted, predicted);
            DispatchParticles(collisionShader, containerKernel);

            RecordNeighborSearch();
            RecordNeighborList();

            var dc = densityConstraintShader;
            for (int i = 0; i < Mathf.Max(1, settings.solverIterations); i++)
            {
                BindNeighborList(dc, lambdaKernel);
                cmd.SetComputeBufferParam(dc, lambdaKernel, Ids.FluidState, fluidState);
                cmd.SetComputeBufferParam(dc, lambdaKernel, Ids.Predicted, predicted);
                cmd.SetComputeBufferParam(dc, lambdaKernel, Ids.Density, density);
                cmd.SetComputeBufferParam(dc, lambdaKernel, Ids.Lambda, lambda);
                DispatchParticles(dc, lambdaKernel);

                BindNeighborList(dc, deltaKernel);
                cmd.SetComputeBufferParam(dc, deltaKernel, Ids.FluidState, fluidState);
                cmd.SetComputeBufferParam(dc, deltaKernel, Ids.Predicted, predicted);
                cmd.SetComputeBufferParam(dc, deltaKernel, Ids.Lambda, lambda);
                cmd.SetComputeBufferParam(dc, deltaKernel, Ids.DeltaPosition, deltaPosition);
                DispatchParticles(dc, deltaKernel);

                cmd.SetComputeBufferParam(dc, applyDeltaKernel, Ids.FluidState, fluidState);
                cmd.SetComputeBufferParam(dc, applyDeltaKernel, Ids.Predicted, predicted);
                cmd.SetComputeBufferParam(dc, applyDeltaKernel, Ids.DeltaPosition, deltaPosition);
                DispatchParticles(dc, applyDeltaKernel);

                cmd.SetComputeBufferParam(collisionShader, containerKernel, Ids.Predicted, predicted);
                DispatchParticles(collisionShader, containerKernel);
            }

            var vs = velocityShader;
            cmd.SetComputeBufferParam(vs, updateVelocityKernel, Ids.FluidState, fluidState);
            cmd.SetComputeBufferParam(vs, updateVelocityKernel, Ids.Positions, positions);
            cmd.SetComputeBufferParam(vs, updateVelocityKernel, Ids.Predicted, predicted);
            cmd.SetComputeBufferParam(vs, updateVelocityKernel, Ids.Velocities, velocities);
            cmd.SetComputeBufferParam(vs, updateVelocityKernel, Ids.Density, density);
            DispatchParticles(vs, updateVelocityKernel);

            BindNeighborList(vs, viscosityKernel);
            cmd.SetComputeBufferParam(vs, viscosityKernel, Ids.FluidState, fluidState);
            cmd.SetComputeBufferParam(vs, viscosityKernel, Ids.Predicted, predicted);
            cmd.SetComputeBufferParam(vs, viscosityKernel, Ids.Velocities, velocities);
            cmd.SetComputeBufferParam(vs, viscosityKernel, Ids.VelocitiesOut, velocitiesAlt);
            DispatchParticles(vs, viscosityKernel);
            Swap(ref velocities, ref velocitiesAlt);
        }

        void RecordNeighborSearch()
        {
            var ns = neighborSearchShader;

            cmd.SetComputeBufferParam(ns, clearCountsKernel, Ids.CellCount, cellCount);
            cmd.DispatchCompute(ns, clearCountsKernel, Groups(hashTableSize + 1, ThreadGroupSize), 1, 1);

            cmd.SetComputeBufferParam(ns, hashKernel, Ids.Predicted, predicted);
            cmd.SetComputeBufferParam(ns, hashKernel, Ids.CellCount, cellCount);
            cmd.SetComputeBufferParam(ns, hashKernel, Ids.ParticleCell, particleCell);
            DispatchParticles(ns, hashKernel);

            RecordScan(cellCount, cellStart, hashTableSize + 1, 0);

            cmd.SetComputeBufferParam(ns, aliveCountKernel, Ids.CellStart, cellStart);
            cmd.SetComputeBufferParam(ns, aliveCountKernel, Ids.FluidStateOut, fluidState);
            cmd.DispatchCompute(ns, aliveCountKernel, 1, 1, 1);

            cmd.SetComputeBufferParam(ns, reorderKernel, Ids.CellStart, cellStart);
            cmd.SetComputeBufferParam(ns, reorderKernel, Ids.ParticleCell, particleCell);
            cmd.SetComputeBufferParam(ns, reorderKernel, Ids.Positions, positions);
            cmd.SetComputeBufferParam(ns, reorderKernel, Ids.Predicted, predicted);
            cmd.SetComputeBufferParam(ns, reorderKernel, Ids.Velocities, velocities);
            cmd.SetComputeBufferParam(ns, reorderKernel, Ids.FluidType, fluidType);
            cmd.SetComputeBufferParam(ns, reorderKernel, Ids.PositionsOut, positionsAlt);
            cmd.SetComputeBufferParam(ns, reorderKernel, Ids.PredictedOut, predictedAlt);
            cmd.SetComputeBufferParam(ns, reorderKernel, Ids.VelocitiesOut, velocitiesAlt);
            cmd.SetComputeBufferParam(ns, reorderKernel, Ids.FluidTypeOut, fluidTypeAlt);
            cmd.SetComputeBufferParam(ns, reorderKernel, Ids.SortedCellId, sortedCellId);
            DispatchParticles(ns, reorderKernel);

            Swap(ref positions, ref positionsAlt);
            Swap(ref predicted, ref predictedAlt);
            Swap(ref velocities, ref velocitiesAlt);
            Swap(ref fluidType, ref fluidTypeAlt);
        }

        void RecordNeighborList()
        {
            var nl = neighborListShader;
            cmd.SetComputeBufferParam(nl, neighborListKernel, Ids.FluidState, fluidState);
            cmd.SetComputeBufferParam(nl, neighborListKernel, Ids.CellStart, cellStart);
            cmd.SetComputeBufferParam(nl, neighborListKernel, Ids.CellCount, cellCount);
            cmd.SetComputeBufferParam(nl, neighborListKernel, Ids.SortedCellId, sortedCellId);
            cmd.SetComputeBufferParam(nl, neighborListKernel, Ids.Predicted, predicted);
            cmd.SetComputeBufferParam(nl, neighborListKernel, Ids.NeighborListOut, neighborList);
            cmd.SetComputeBufferParam(nl, neighborListKernel, Ids.NeighborCountOut, neighborCount);
            DispatchParticles(nl, neighborListKernel);
        }

        void RecordScan(GraphicsBuffer input, GraphicsBuffer output, int count, int level)
        {
            var ns = neighborSearchShader;
            int blocks = Groups(count, ScanBlockSize);

            cmd.SetComputeIntParam(ns, Ids.ScanCount, count);
            cmd.SetComputeBufferParam(ns, scanKernel, Ids.ScanInput, input);
            cmd.SetComputeBufferParam(ns, scanKernel, Ids.ScanOutput, output);
            cmd.SetComputeBufferParam(ns, scanKernel, Ids.ScanBlockSums, scanBlockSums[level]);
            cmd.DispatchCompute(ns, scanKernel, blocks, 1, 1);

            if (blocks <= 1)
                return;

            RecordScan(scanBlockSums[level], scanBlockOffsets[level], blocks, level + 1);

            cmd.SetComputeIntParam(ns, Ids.ScanCount, count);
            cmd.SetComputeBufferParam(ns, addOffsetsKernel, Ids.ScanOutput, output);
            cmd.SetComputeBufferParam(ns, addOffsetsKernel, Ids.ScanBlockOffsets, scanBlockOffsets[level]);
            cmd.DispatchCompute(ns, addOffsetsKernel, blocks, 1, 1);
        }

        void BindNeighborList(ComputeShader cs, int kernel)
        {
            cmd.SetComputeBufferParam(cs, kernel, Ids.NeighborList, neighborList);
            cmd.SetComputeBufferParam(cs, kernel, Ids.NeighborCount, neighborCount);
        }

        void DispatchParticles(ComputeShader cs, int kernel) =>
            cmd.DispatchCompute(cs, kernel, Groups(poolSize, ThreadGroupSize), 1, 1);

        static int Groups(int count, int groupSize) => (count + groupSize - 1) / groupSize;

        static void Swap(ref GraphicsBuffer a, ref GraphicsBuffer b) => (a, b) = (b, a);

        void OnDrawGizmos()
        {
            Gizmos.color = new Color(0.3f, 0.7f, 1f, 0.8f);
            Gizmos.DrawWireCube(container.center, container.size);
        }

        static class Ids
        {
            public static readonly int ParticleCount = Shader.PropertyToID("_ParticleCount");
            public static readonly int DeltaTime = Shader.PropertyToID("_DeltaTime");
            public static readonly int Gravity = Shader.PropertyToID("_Gravity");
            public static readonly int MaxSpeed = Shader.PropertyToID("_MaxSpeed");
            public static readonly int SmoothingRadius = Shader.PropertyToID("_SmoothingRadius");
            public static readonly int SmoothingRadiusSq = Shader.PropertyToID("_SmoothingRadiusSq");
            public static readonly int InvCellSize = Shader.PropertyToID("_InvCellSize");
            public static readonly int GridBitsX = Shader.PropertyToID("_GridBitsX");
            public static readonly int GridBitsY = Shader.PropertyToID("_GridBitsY");
            public static readonly int GridBitsZ = Shader.PropertyToID("_GridBitsZ");
            public static readonly int HashTableSize = Shader.PropertyToID("_HashTableSize");
            public static readonly int ParticleMass = Shader.PropertyToID("_ParticleMass");
            public static readonly int InvRestDensity = Shader.PropertyToID("_InvRestDensity");
            public static readonly int Poly6Coeff = Shader.PropertyToID("_Poly6Coeff");
            public static readonly int SpikyGradCoeff = Shader.PropertyToID("_SpikyGradCoeff");
            public static readonly int ConstraintRelaxation = Shader.PropertyToID("_ConstraintRelaxation");
            public static readonly int Cohesion = Shader.PropertyToID("_Cohesion");
            public static readonly int SCorrK = Shader.PropertyToID("_SCorrK");
            public static readonly int SCorrInvWdq = Shader.PropertyToID("_SCorrInvWdq");
            public static readonly int Viscosity = Shader.PropertyToID("_Viscosity");
            public static readonly int AerationImpactMin = Shader.PropertyToID("_AerationImpactMin");
            public static readonly int AerationImpactRange = Shader.PropertyToID("_AerationImpactRange");
            public static readonly int AerationDecay = Shader.PropertyToID("_AerationDecay");
            public static readonly int AerationBuoyancy = Shader.PropertyToID("_AerationBuoyancy");
            public static readonly int AerationDiffusion = Shader.PropertyToID("_AerationDiffusion");
            public static readonly int ContainerMin = Shader.PropertyToID("_ContainerMin");
            public static readonly int ContainerMax = Shader.PropertyToID("_ContainerMax");

            public static readonly int Positions = Shader.PropertyToID("_Positions");
            public static readonly int PositionsOut = Shader.PropertyToID("_PositionsOut");
            public static readonly int Predicted = Shader.PropertyToID("_Predicted");
            public static readonly int PredictedOut = Shader.PropertyToID("_PredictedOut");
            public static readonly int Velocities = Shader.PropertyToID("_Velocities");
            public static readonly int VelocitiesOut = Shader.PropertyToID("_VelocitiesOut");
            public static readonly int FluidType = Shader.PropertyToID("_FluidType");
            public static readonly int FluidTypeOut = Shader.PropertyToID("_FluidTypeOut");
            public static readonly int Density = Shader.PropertyToID("_Density");
            public static readonly int Lambda = Shader.PropertyToID("_Lambda");
            public static readonly int DeltaPosition = Shader.PropertyToID("_DeltaPosition");

            public static readonly int CellCount = Shader.PropertyToID("_CellCount");
            public static readonly int CellStart = Shader.PropertyToID("_CellStart");
            public static readonly int ParticleCell = Shader.PropertyToID("_ParticleCell");
            public static readonly int SortedCellId = Shader.PropertyToID("_SortedCellId");
            public static readonly int NeighborList = Shader.PropertyToID("_NeighborList");
            public static readonly int NeighborCount = Shader.PropertyToID("_NeighborCount");
            public static readonly int NeighborListOut = Shader.PropertyToID("_NeighborListOut");
            public static readonly int NeighborCountOut = Shader.PropertyToID("_NeighborCountOut");

            public static readonly int FluidState = Shader.PropertyToID("_FluidState");
            public static readonly int FluidStateOut = Shader.PropertyToID("_FluidStateOut");
            public static readonly int SinkCount = Shader.PropertyToID("_SinkCount");
            public static readonly int Sinks = Shader.PropertyToID("_Sinks");
            public static readonly int SinkCounters = Shader.PropertyToID("_SinkCounters");
            public static readonly int SpawnLayerCount = Shader.PropertyToID("_SpawnLayerCount");
            public static readonly int SpawnPointCount = Shader.PropertyToID("_SpawnPointCount");
            public static readonly int SpawnJitter = Shader.PropertyToID("_SpawnJitter");
            public static readonly int FrameSeed = Shader.PropertyToID("_FrameSeed");
            public static readonly int SpawnLayers = Shader.PropertyToID("_SpawnLayers");
            public static readonly int SpawnPattern = Shader.PropertyToID("_SpawnPattern");

            public static readonly int ScanCount = Shader.PropertyToID("_ScanCount");
            public static readonly int ScanInput = Shader.PropertyToID("_ScanInput");
            public static readonly int ScanOutput = Shader.PropertyToID("_ScanOutput");
            public static readonly int ScanBlockSums = Shader.PropertyToID("_ScanBlockSums");
            public static readonly int ScanBlockOffsets = Shader.PropertyToID("_ScanBlockOffsets");
        }
    }
}
