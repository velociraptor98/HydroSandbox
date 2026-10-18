using UnityEngine;

namespace HydroSandbox
{
    public enum FluidViewMode
    {
        Spheres,
        Water,
    }

    /// <summary>
    /// On-screen tuning and timing panel for the solver. F1 toggles the panel, F2 switches between
    /// the debug spheres and the screen-space water surface.
    /// </summary>
    public class FluidDebugPanel : MonoBehaviour
    {
        static readonly int[] CountPresets = { 32768, 65536, 131072, 262144, 524288 };
        static readonly string[] ViewModeLabels = { "Spheres", "Water" };

        [SerializeField] FluidSolver solver;
        [SerializeField] bool visible = true;

        [Header("View")]
        [SerializeField] FluidDebugRenderer sphereRenderer;
        [SerializeField] FluidSurface waterSurface;
        [SerializeField] FluidViewMode viewMode = FluidViewMode.Spheres;

        readonly FrameTiming[] frameTimings = new FrameTiming[1];
        float smoothedFrameMs = 16.7f;
        double gpuFrameMs;

        bool CanSwitchView => sphereRenderer != null && waterSurface != null;

        public FluidViewMode ViewMode
        {
            get => viewMode;
            set
            {
                viewMode = value;
                ApplyViewMode();
            }
        }

        void Start() => ApplyViewMode();

        void ApplyViewMode()
        {
            if (!CanSwitchView)
                return;
            sphereRenderer.enabled = viewMode == FluidViewMode.Spheres;
            waterSurface.enabled = viewMode == FluidViewMode.Water;
        }

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
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.F2 && CanSwitchView)
            {
                ViewMode = viewMode == FluidViewMode.Spheres ? FluidViewMode.Water : FluidViewMode.Spheres;
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
            GUILayout.Label($"Particles {solver.AliveCount:N0} / {solver.Capacity:N0}");

            if (CanSwitchView)
            {
                var selected = (FluidViewMode)GUILayout.Toolbar((int)viewMode, ViewModeLabels);
                if (selected != viewMode)
                    ViewMode = selected;
            }

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
            s.viscosity = Slider("Viscosity", s.viscosity, 0f, 0.2f, "F3");
            s.vorticityConfinement = Slider("Vorticity confinement", s.vorticityConfinement, 0f, 1f, "F2");
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
