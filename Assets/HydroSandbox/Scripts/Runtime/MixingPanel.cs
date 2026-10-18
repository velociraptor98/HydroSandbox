using UnityEngine;

namespace HydroSandbox
{
    /// <summary>
    /// On-screen controls for a multi-liquid scene: switch each source on or off, edit each
    /// liquid's density and viscosity, and make each pair of liquids miscible or immiscible.
    /// F1 toggles the panel.
    /// </summary>
    public class MixingPanel : MonoBehaviour
    {
        [SerializeField] FluidSolver solver;
        [SerializeField] FluidMixing mixing;
        [SerializeField] FluidSource[] sources = System.Array.Empty<FluidSource>();
        [SerializeField] bool visible = true;

        float smoothedFrameMs = 16.7f;
        Vector2 scroll;

        /// <summary>The screen area (GUI coordinates, origin top left) the panel covers; empty while hidden.</summary>
        public Rect ScreenRect => visible && solver != null && mixing != null ? new Rect(10, 10, 300, Screen.height - 20) : Rect.zero;

        void Update() => smoothedFrameMs = Mathf.Lerp(smoothedFrameMs, Time.unscaledDeltaTime * 1000f, 0.05f);

        void OnGUI()
        {
            Event e = Event.current;
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.F1)
            {
                visible = !visible;
                e.Use();
            }
            if (!visible || solver == null || mixing == null)
                return;

            GUILayout.BeginArea(ScreenRect);
            GUILayout.BeginVertical(GUI.skin.box);
            scroll = GUILayout.BeginScrollView(scroll);

            GUILayout.Label($"{1000f / smoothedFrameMs:F0} FPS   particles {solver.AliveCount:N0} / {solver.Capacity:N0}");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(solver.Paused ? "Resume" : "Pause"))
                solver.Paused = !solver.Paused;
            if (GUILayout.Button("Reset"))
                solver.ResetSimulation();
            GUILayout.EndHorizontal();

            GUILayout.Space(6);
            GUILayout.Label("Pour (or click a nozzle)");
            foreach (FluidSource source in sources)
            {
                if (source == null)
                    continue;
                string liquid = source.Liquid < mixing.LiquidCount ? mixing.Liquids[source.Liquid].name : "?";
                source.enabled = GUILayout.Toggle(source.enabled, $" {liquid}");
            }

            FluidLiquid[] liquids = mixing.Liquids;
            for (int i = 0; i < mixing.LiquidCount; i++)
            {
                FluidLiquid l = liquids[i];
                GUILayout.Space(6);
                GUILayout.BeginHorizontal();
                Swatch(l.swatch);
                GUILayout.Label(l.name);
                GUILayout.EndHorizontal();
                l.density = Slider("Density (kg/m³)", l.density, 500f, 2000f, "F0");
                l.viscosity = Slider("Viscosity", l.viscosity, 0f, 1f, "F2");
                l.wallFriction = Slider("Wall cling", l.wallFriction, 0f, 1f, "F2");
            }

            GUILayout.Space(6);
            GUILayout.Label("Miscibility");
            for (int a = 0; a < mixing.LiquidCount; a++)
            for (int b = a + 1; b < mixing.LiquidCount; b++)
            {
                FluidPairing p = mixing.FindPairing(a, b);
                if (p == null)
                    continue;
                p.miscible = GUILayout.Toggle(p.miscible, $" {liquids[a].name} + {liquids[b].name}: {(p.miscible ? "mix" : "separate")}");
                if (p.miscible)
                    p.diffusion = Slider("   Diffusion (1/s)", p.diffusion, 0f, 5f, "F2");
                p.tension = Slider("   Interface tension", p.tension, 0f, 80f, "F0");
            }

            GUILayout.EndScrollView();
            GUILayout.EndVertical();
            GUILayout.EndArea();
        }

        static void Swatch(Color color)
        {
            Rect r = GUILayoutUtility.GetRect(14, 14, GUILayout.Width(14));
            Color previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = previous;
        }

        static float Slider(string label, float value, float min, float max, string format)
        {
            GUILayout.Label($"{label}: {value.ToString(format)}");
            return GUILayout.HorizontalSlider(value, min, max);
        }
    }
}
