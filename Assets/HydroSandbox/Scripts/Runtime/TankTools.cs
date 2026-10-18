using UnityEngine;
using UnityEngine.InputSystem;

namespace HydroSandbox
{
    /// <summary>
    /// Mouse tools for a <see cref="FluidTank"/> scene. Clicking a <see cref="PourNozzle"/> starts
    /// or stops its pour. With the drill on (H, or the toolbar), clicking a side wall of the tank
    /// drills a hole through the glass and the liquid leaks out. A click is a press and release
    /// without dragging, so it does not fight the orbit camera.
    /// </summary>
    public class TankTools : MonoBehaviour
    {
        const float ClickSlopPixels = 6f;
        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");

        [SerializeField] FluidTank tank;
        [Tooltip("Names the liquids in the nozzle tooltips.")]
        [SerializeField] FluidMixing mixing;
        [Tooltip("Optional: clicks over its panel are ignored.")]
        [SerializeField] MixingPanel panel;
        [SerializeField] bool drilling;
        [SerializeField, Range(0.01f, 0.04f)] float holeRadius = 0.018f;
        [SerializeField] Material previewMaterial;

        Camera view;
        PourNozzle hoveredNozzle;
        bool pressValid;
        Vector2 pressPosition;
        bool hasDrillTarget;
        Vector3 drillPoint, drillNormal;
        GameObject preview;
        float previewRadius;
        Rect toolbarRect;

        public void Configure(FluidTank targetTank, FluidMixing liquids, MixingPanel mixingPanel, Material holePreview)
        {
            tank = targetTank;
            mixing = liquids;
            panel = mixingPanel;
            previewMaterial = holePreview;
        }

        void OnDisable()
        {
            SetHovered(null);
            if (preview != null)
            {
                Destroy(preview.GetComponent<MeshFilter>().sharedMesh);
                Destroy(preview);
            }
        }

        void Update()
        {
            Mouse mouse = Mouse.current;
            Keyboard keyboard = Keyboard.current;
            if (keyboard != null && keyboard.hKey.wasPressedThisFrame)
                drilling = !drilling;
            if (view == null)
                view = Camera.main;
            if (mouse == null || view == null)
                return;

            Vector2 screen = mouse.position.ReadValue();
            bool overUI = OverUI(screen);
            PourNozzle nozzle = null;
            hasDrillTarget = false;
            if (!overUI && Physics.Raycast(view.ScreenPointToRay(screen), out RaycastHit hit, 100f, ~0, QueryTriggerInteraction.Ignore))
            {
                nozzle = hit.collider.GetComponentInParent<PourNozzle>();
                if (nozzle == null && drilling && tank != null && hit.collider.transform.IsChildOf(tank.transform))
                {
                    hasDrillTarget = tank.TryPlaceHole(hit.point, hit.normal, holeRadius, out _, out _);
                    drillPoint = hit.point;
                    drillNormal = hit.normal;
                }
            }
            SetHovered(nozzle);
            UpdatePreview();

            if (mouse.leftButton.wasPressedThisFrame)
            {
                pressValid = !overUI && GUIUtility.hotControl == 0;
                pressPosition = screen;
            }
            if ((screen - pressPosition).sqrMagnitude > ClickSlopPixels * ClickSlopPixels)
                pressValid = false;
            if (mouse.leftButton.wasReleasedThisFrame && pressValid)
            {
                pressValid = false;
                if (nozzle != null)
                    nozzle.Toggle();
                else if (hasDrillTarget)
                    tank.TryDrill(drillPoint, drillNormal, holeRadius);
            }
        }

        bool OverUI(Vector2 screen)
        {
            var gui = new Vector2(screen.x, Screen.height - screen.y);
            return toolbarRect.Contains(gui) || (panel != null && panel.ScreenRect.Contains(gui));
        }

        void SetHovered(PourNozzle nozzle)
        {
            if (hoveredNozzle == nozzle)
                return;
            if (hoveredNozzle != null)
                hoveredNozzle.Hovered = false;
            hoveredNozzle = nozzle;
            if (nozzle != null)
                nozzle.Hovered = true;
        }

        void UpdatePreview()
        {
            if (!hasDrillTarget || previewMaterial == null)
            {
                if (preview != null)
                    preview.SetActive(false);
                return;
            }
            if (preview == null)
            {
                preview = new GameObject("Hole Preview");
                preview.AddComponent<MeshFilter>();
                var renderer = preview.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = previewMaterial;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                var block = new MaterialPropertyBlock();
                block.SetColor(BaseColor, new Color(1f, 0.55f, 0.15f, 0.8f));
                renderer.SetPropertyBlock(block);
            }
            if (!Mathf.Approximately(previewRadius, holeRadius))
            {
                var filter = preview.GetComponent<MeshFilter>();
                Destroy(filter.sharedMesh);
                filter.sharedMesh = FluidTank.HoleEdgeMesh(holeRadius, tank.WallThickness * 1.2f, 0);
                previewRadius = holeRadius;
            }
            tank.TryPlaceHole(drillPoint, drillNormal, holeRadius, out Vector3 center, out Vector3 axis);
            preview.transform.SetPositionAndRotation(center, Quaternion.LookRotation(axis));
            preview.SetActive(true);
        }

        void OnGUI()
        {
            if (tank == null)
                return;
            const float width = 560f, height = 58f;
            toolbarRect = new Rect((Screen.width - width) * 0.5f, Screen.height - height - 10f, width, height);
            GUILayout.BeginArea(toolbarRect, GUI.skin.box);
            GUILayout.BeginHorizontal();
            drilling = GUILayout.Toggle(drilling, drilling ? " Drill: on (H)" : " Drill: off (H)", GUILayout.Width(120));
            GUILayout.Label($"Hole Ø {holeRadius * 200f:F1} cm", GUILayout.Width(90));
            holeRadius = GUILayout.HorizontalSlider(holeRadius, 0.01f, 0.04f, GUILayout.Width(120));
            GUILayout.FlexibleSpace();
            GUI.enabled = tank.HoleCount > 0;
            if (GUILayout.Button($"Seal holes ({tank.HoleCount})", GUILayout.Width(130)))
                tank.SealAll();
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.Label(drilling
                ? "Click a side of the tank to drill a hole. Click a nozzle to pour."
                : "Click a nozzle to start or stop its pour. Drag to orbit, scroll to zoom.");
            GUILayout.EndArea();

            if (hoveredNozzle != null && hoveredNozzle.Source != null && Mouse.current != null)
            {
                int liquid = hoveredNozzle.Source.Liquid;
                string name = mixing != null && liquid < mixing.LiquidCount ? mixing.Liquids[liquid].name : "liquid";
                Vector2 m = Mouse.current.position.ReadValue();
                var label = new Rect(m.x + 16f, Screen.height - m.y + 8f, 160f, 24f);
                GUI.Label(label, hoveredNozzle.Pouring ? $"Stop {name}" : $"Pour {name}", GUI.skin.box);
            }
        }
    }
}
