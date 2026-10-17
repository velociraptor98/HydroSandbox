using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace HydroSandbox
{
    /// <summary>
    /// Runs the dam break at a series of particle counts and logs frame and GPU timings for each.
    /// The container and camera scale with the count so fill fraction, column height relative to
    /// the box, and on-screen coverage stay comparable between runs.
    /// </summary>
    public class FluidBenchmark : MonoBehaviour
    {
        [SerializeField] FluidSolver solver;
        [SerializeField] Transform viewCamera;
        [SerializeField] int[] particleCounts = { 32768, 65536, 131072, 163840, 196608, 229376, 262144, 393216, 524288 };
        [SerializeField, Min(0)] int warmupFrames = 60;
        [SerializeField, Min(1)] int sampleFrames = 300;

        const string CommandLineFlag = "-hydroBenchmark";

        bool quitWhenDone;

        public bool IsRunning { get; private set; }
        public string Report { get; private set; } = "";

        /// <summary>
        /// Launching a player with -hydroBenchmark runs the benchmark, logs it, and quits.
        /// -hydroSubsteps N and -hydroIterations N override the scene's solver settings.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnableFromCommandLine()
        {
            if (System.Array.IndexOf(System.Environment.GetCommandLineArgs(), CommandLineFlag) < 0)
                return;
            var benchmark = FindAnyObjectByType<FluidBenchmark>(FindObjectsInactive.Include);
            if (benchmark == null)
                return;
            benchmark.quitWhenDone = true;
            benchmark.enabled = true;
            Application.runInBackground = true;

            FluidSolverSettings settings = benchmark.solver.Settings;
            if (TryGetIntArgument("-hydroSubsteps", out int substeps))
                settings.substeps = substeps;
            if (TryGetIntArgument("-hydroIterations", out int iterations))
                settings.solverIterations = iterations;
        }

        static bool TryGetIntArgument(string name, out int value)
        {
            string[] args = System.Environment.GetCommandLineArgs();
            int index = System.Array.IndexOf(args, name);
            value = 0;
            return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out value);
        }

        IEnumerator Start()
        {
            if (solver == null)
                yield break;

            IsRunning = true;
            yield return null;

            int previousVSync = QualitySettings.vSyncCount;
            int previousTargetFrameRate = Application.targetFrameRate;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;

            Bounds baseContainer = solver.Container;
            int baseCount = Mathf.Max(1, solver.ParticleCount);
            Vector3 cameraOffset = viewCamera != null ? viewCamera.position - baseContainer.center : Vector3.zero;

            var report = new StringBuilder();
            report.AppendLine($"HydroSandbox benchmark | {SystemInfo.graphicsDeviceName} | {SystemInfo.graphicsDeviceType} | " +
                              $"substeps {solver.Settings.substeps}, iterations {solver.Settings.solverIterations}, " +
                              $"radius {solver.Settings.particleRadius} | {Screen.width}x{Screen.height}");
            report.AppendLine("particles | frame avg ms | frame p95 ms | fps avg | GPU frame avg ms | solver GPU avg ms");

            var timings = new FrameTiming[1];
            var frameMs = new List<float>(sampleFrames);
            int best60 = 0;

            foreach (int count in particleCounts)
            {
                float scale = Mathf.Pow(count / (float)baseCount, 1f / 3f);
                Vector3 size = baseContainer.size * scale;
                Vector3 center = new Vector3(baseContainer.center.x, baseContainer.min.y + size.y * 0.5f, baseContainer.center.z);
                solver.Container = new Bounds(center, size);
                if (viewCamera != null)
                    viewCamera.position = center + cameraOffset * scale;
                solver.SetParticleCount(count);

                for (int i = 0; i < warmupFrames; i++)
                    yield return null;

                frameMs.Clear();
                double gpuFrameSum = 0, solverSum = 0;
                int gpuFrameSamples = 0;
                for (int i = 0; i < sampleFrames; i++)
                {
                    yield return null;
                    frameMs.Add(Time.unscaledDeltaTime * 1000f);
                    solverSum += solver.GpuSolveMilliseconds;
                    FrameTimingManager.CaptureFrameTimings();
                    if (FrameTimingManager.GetLatestTimings(1, timings) > 0 && timings[0].gpuFrameTime > 0)
                    {
                        gpuFrameSum += timings[0].gpuFrameTime;
                        gpuFrameSamples++;
                    }
                }

                float average = 0;
                foreach (float ms in frameMs)
                    average += ms;
                average /= frameMs.Count;
                frameMs.Sort();
                float p95 = frameMs[Mathf.Min(frameMs.Count - 1, Mathf.CeilToInt(frameMs.Count * 0.95f) - 1)];
                string gpuFrame = gpuFrameSamples > 0 ? (gpuFrameSum / gpuFrameSamples).ToString("F2") : "n/a";
                if (p95 <= 1000f / 60f)
                    best60 = Mathf.Max(best60, count);

                string solverGpu = solver.HasGpuSolveTiming ? (solverSum / sampleFrames).ToString("F2") : "n/a";
                report.AppendLine($"{count,9} | {average,12:F2} | {p95,12:F2} | {1000f / average,7:F0} | {gpuFrame,16} | {solverGpu,17}");
            }

            report.AppendLine(best60 > 0
                ? $"Largest count with p95 frame time within 60 FPS: {best60:N0}"
                : "No tested count held 60 FPS at p95.");

            solver.Container = baseContainer;
            if (viewCamera != null)
                viewCamera.position = baseContainer.center + cameraOffset;
            solver.SetParticleCount(baseCount);
            QualitySettings.vSyncCount = previousVSync;
            Application.targetFrameRate = previousTargetFrameRate;

            Report = report.ToString();
            Debug.Log(Report);
            IsRunning = false;
            if (quitWhenDone)
                Application.Quit();
        }
    }
}
