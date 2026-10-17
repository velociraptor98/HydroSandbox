using UnityEngine;

namespace HydroSandbox
{
    /// <summary>On-screen tuning and timing panel for the solver. F1 toggles it.</summary>
    public class FluidDebugPanel : MonoBehaviour
    {
        static readonly int[] CountPresets = { 32768, 65536, 131072, 262144, 524288 };

        [SerializeField] FluidSolver solver;
        [SerializeField] bool visible = true;

        readonly FrameTiming[] frameTimings = new FrameTiming[1];
        float smoothedFrameMs = 16.7f;
        double gpuFrameMs;

        void Update()
        {
            smoothedFrameMs = Mathf.Lerp(smoothedFrameMs, Time.unscaledDeltaTime * 1000f, 0.05f);
            FrameTimingManager.CaptureFrameTimings();
            if (FrameTimingManager.GetLatestTimings(1, frameTimings) > 0)
                gpuFrameMs = frameTimings[0].gpuFrameTime;
        }

        void OnGUI()
        {
            Event e = Event.current;
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.F1)
            {
                visible = !visible;
                e.Use();
            }
            if (!visible || solver == null)
                return;

            FluidSolverSettings s = solver.Settings;
            GUILayout.BeginArea(new Rect(10, 10, 320, Screen.height - 20));
            GUILayout.BeginVertical(GUI.skin.box);

            GUILayout.Label($"{1000f / smoothedFrameMs:F0} FPS  ({smoothedFrameMs:F2} ms)");
            string solverMs = solver.HasGpuSolveTiming ? $"{solver.GpuSolveMilliseconds:F2} ms" : "n/a";
            string frameGpuMs = gpuFrameMs > 0 ? $"{gpuFrameMs:F2} ms" : "n/a";
            GUILayout.Label($"GPU frame {frameGpuMs}   solver {solverMs}");
            GUILayout.Label($"Particles {solver.ParticleCount:N0}");

            GUILayout.BeginHorizontal();
            foreach (int count in CountPresets)
            {
                if (GUILayout.Button($"{count / 1024}k"))
                    solver.SetParticleCount(count);
            }
            GUILayout.EndHorizontal();

            s.substeps = IntSlider("Substeps", s.substeps, 1, 8);
            s.solverIterations = IntSlider("Iterations", s.solverIterations, 1, 8);
            s.particleRadius = Slider("Particle radius", s.particleRadius, 0.005f, 0.1f, "F3");
            s.restDensity = Slider("Rest density", s.restDensity, 500f, 2000f, "F0");
            s.viscosity = Slider("Viscosity", s.viscosity, 0f, 1f, "F2");
            s.gravity.y = Slider("Gravity Y", s.gravity.y, -20f, 0f, "F2");

            GUILayout.BeginHorizontal();
            if (GUILayout.Button(solver.Paused ? "Resume" : "Pause"))
                solver.Paused = !solver.Paused;
            if (GUILayout.Button("Reset"))
                solver.ResetSimulation();
            GUILayout.EndHorizontal();

            GUILayout.EndVertical();
            GUILayout.EndArea();
        }

        static float Slider(string label, float value, float min, float max, string format)
        {
            GUILayout.Label($"{label}: {value.ToString(format)}");
            return GUILayout.HorizontalSlider(value, min, max);
        }

        static int IntSlider(string label, int value, int min, int max)
        {
            GUILayout.Label($"{label}: {value}");
            return Mathf.RoundToInt(GUILayout.HorizontalSlider(value, min, max));
        }
    }
}
