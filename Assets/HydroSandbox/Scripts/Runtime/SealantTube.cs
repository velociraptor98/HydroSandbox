using UnityEngine;

namespace HydroSandbox
{
    /// <summary>A tube of glass sealant: clicking a drilled hole plugs it.</summary>
    public class SealantTube : BenchItem
    {
        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");

        [Tooltip("Distance from the pivot to the nozzle's tip along local +Z.")]
        [SerializeField, Min(0f)] float tipOffset = 0.1f;
        [SerializeField] Material highlightMaterial;

        GameObject highlight;
        int highlightedHole = -1;
        int highlightedCount = -1;

        public void Configure(float nozzleTipOffset, Material holeHighlight)
        {
            tipOffset = nozzleTipOffset;
            highlightMaterial = holeHighlight;
        }

        internal override bool Hold(in HandInput input)
        {
            FluidTank tank = input.tank;
            if (tank != null && TryGlassHit(input, out RaycastHit glass))
            {
                Vector3 outward = glass.normal;
                Vector3 tip = glass.point + outward * 0.006f;
                int hole = tank.FindHole(glass.point, glass.normal, 0.02f);
                if (hole >= 0)
                {
                    Vector3 axis = tank.HoleAxis(hole);
                    outward = Vector3.Dot(axis, glass.normal) >= 0f ? axis : -axis;
                    tip = tank.HoleCenter(hole) + outward * (tank.WallThickness * 0.5f + 0.006f);
                }
                transform.SetPositionAndRotation(tip + outward * tipOffset, Quaternion.LookRotation(-outward, Vector3.up));
                ShowHighlight(tank, hole);
                if (hole >= 0 && input.useClicked)
                {
                    tank.Seal(hole);
                    ShowHighlight(tank, -1);
                }
                return true;
            }

            ShowHighlight(tank, -1);
            Vector3 aim = input.ray.direction;
            transform.SetPositionAndRotation(input.planePoint - aim * tipOffset, Quaternion.LookRotation(aim, Vector3.up));
            return true;
        }

        internal override void PutBack()
        {
            ShowHighlight(null, -1);
            base.PutBack();
        }

        void ShowHighlight(FluidTank tank, int hole)
        {
            if (hole < 0 || highlightMaterial == null)
            {
                if (highlight != null)
                    highlight.SetActive(false);
                highlightedHole = -1;
                return;
            }
            if (highlight == null)
            {
                highlight = new GameObject("Seal Highlight");
                highlight.AddComponent<MeshFilter>();
                var renderer = highlight.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = highlightMaterial;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                var block = new MaterialPropertyBlock();
                block.SetColor(BaseColor, new Color(0.3f, 1f, 0.45f, 0.85f));
                renderer.SetPropertyBlock(block);
            }
            if (hole != highlightedHole || tank.HoleCount != highlightedCount)
            {
                var filter = highlight.GetComponent<MeshFilter>();
                Destroy(filter.sharedMesh);
                filter.sharedMesh = FluidTank.HoleEdgeMesh(tank.HoleRadius(hole) * 1.15f, tank.WallThickness * 1.3f, 0);
                highlight.transform.SetPositionAndRotation(tank.HoleCenter(hole), Quaternion.LookRotation(tank.HoleAxis(hole)));
                highlightedHole = hole;
                highlightedCount = tank.HoleCount;
            }
            highlight.SetActive(true);
        }

        void OnDestroy()
        {
            if (highlight != null)
            {
                Destroy(highlight.GetComponent<MeshFilter>().sharedMesh);
                Destroy(highlight);
            }
        }
    }
}
