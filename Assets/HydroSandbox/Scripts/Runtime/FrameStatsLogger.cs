using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace HydroSandbox
{
    /// <summary>
    /// Launching a player with -hydroFrameStats logs frame timings for whatever scene loads, then
    /// quits: 120 warm-up frames, then 600 measured frames with vsync off. Adding
    /// -hydroScreenshot &lt;path&gt; also saves the last frame as a PNG.
    /// </summary>
    public class FrameStatsLogger : MonoBehaviour
    {
        const string CommandLineFlag = "-hydroFrameStats";
        const int WarmupFrames = 120;
        const int SampleFrames = 600;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void EnableFromCommandLine()
        {
            if (System.Array.IndexOf(System.Environment.GetCommandLineArgs(), CommandLineFlag) < 0)
                return;
            Application.runInBackground = true;
            new GameObject(nameof(FrameStatsLogger)).AddComponent<FrameStatsLogger>();
        }

        IEnumerator Start()
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            for (int i = 0; i < WarmupFrames; i++)
                yield return null;

            var frameMs = new List<float>(SampleFrames);
            var timings = new FrameTiming[1];
            double gpuSum = 0;
            int gpuSamples = 0;
            for (int i = 0; i < SampleFrames; i++)
            {
                yield return null;
                frameMs.Add(Time.unscaledDeltaTime * 1000f);
                FrameTimingManager.CaptureFrameTimings();
                if (FrameTimingManager.GetLatestTimings(1, timings) > 0 && timings[0].gpuFrameTime > 0)
                {
                    gpuSum += timings[0].gpuFrameTime;
                    gpuSamples++;
                }
            }

            float average = 0;
            foreach (float ms in frameMs)
                average += ms;
            average /= frameMs.Count;
            frameMs.Sort();
            float p95 = frameMs[Mathf.CeilToInt(frameMs.Count * 0.95f) - 1];
            var solver = FindAnyObjectByType<FluidSolver>();
            string particles = solver != null ? $"{solver.AliveCount:N0} alive / {solver.Capacity:N0}" : "no solver";
            string gpu = gpuSamples > 0 ? (gpuSum / gpuSamples).ToString("F2") : "n/a";
            Debug.Log($"HydroSandbox frame stats | {UnityEngine.SceneManagement.SceneManager.GetActiveScene().name} | " +
                      $"{SystemInfo.graphicsDeviceName} {SystemInfo.graphicsDeviceType} | {Screen.width}x{Screen.height} | " +
                      $"{particles} | frame avg {average:F2} ms, p95 {p95:F2} ms ({1000f / average:F0} FPS) | GPU frame avg {gpu} ms");
            string[] args = System.Environment.GetCommandLineArgs();
            int screenshotArg = System.Array.IndexOf(args, "-hydroScreenshot");
            if (screenshotArg >= 0 && screenshotArg + 1 < args.Length)
            {
                ScreenCapture.CaptureScreenshot(args[screenshotArg + 1]);
                for (int i = 0; i < 3; i++)
                    yield return null;
            }
            Application.Quit();
        }
    }
}
