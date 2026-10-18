using UnityEngine;

namespace HydroSandbox
{
    /// <summary>
    /// A drill for glass. Held over a side of the tank it shows where the hole will go; clicking
    /// drills it, and the scroll wheel changes the bit size.
    /// </summary>
    public class GlassDrill : BenchItem
    {
        static readonly int BaseColor = Shader.PropertyToID("_BaseColor");

        [Tooltip("Spun while held; its local Z is the drilling axis.")]
        [SerializeField] Transform bit;
        [Tooltip("Distance from the pivot to the bit's tip along local +Z.")]
        [SerializeField, Min(0f)] float tipOffset = 0.2f;
        [SerializeField, Range(0.01f, 0.04f)] float holeRadius = 0.018f;
        [SerializeField] Material previewMaterial;

        GameObject preview;
        float previewRadius;
        float spin;

        public override bool CapturesScroll => true;
        public override string HeldHint => $"{holeRadius * 200f:F1} cm bit (scroll to change)\n{base.HeldHint}";

        public void Configure(Transform drillBit, float bitTipOffset, Material holePreview)
        {
            bit = drillBit;
            tipOffset = bitTipOffset;
            previewMaterial = holePreview;
        }

        internal override bool Hold(in HandInput input)
        {
            if (Mathf.Abs(input.scroll) > 0.01f)
                holeRadius = Mathf.Clamp(holeRadius + Mathf.Sign(input.scroll) * 0.002f, 0.01f, 0.04f);

            spin += Time.deltaTime * 1500f;
            if (bit != null)
                bit.localRotation = Quaternion.Euler(0f, 0f, spin);

            FluidTank tank = input.tank;
            if (tank != null && TryGlassHit(input, out RaycastHit glass) &&
                tank.TryPlaceHole(glass.point, glass.normal, holeRadius, out Vector3 center, out Vector3 axis))
            {
                // The axis is the wall's outward direction; face whichever side the cursor is on.
                Vector3 outward = Vector3.Dot(axis, glass.normal) >= 0f ? axis : -axis;
                Vector3 tip = center + outward * (tank.WallThickness * 0.5f + 0.004f);
                transform.SetPositionAndRotation(tip + outward * tipOffset, Quaternion.LookRotation(-outward, Vector3.up));
                ShowPreview(center, axis, tank.WallThickness);
                if (input.useClicked)
                    tank.TryDrill(glass.point, glass.normal, holeRadius);
                return true;
            }

            HidePreview();
            Vector3 aim = input.ray.direction;
            transform.SetPositionAndRotation(input.planePoint - aim * tipOffset, Quaternion.LookRotation(aim, Vector3.up));
            return true;
        }

        internal override void PutBack()
        {
            HidePreview();
            if (bit != null)
                bit.localRotation = Quaternion.identity;
            base.PutBack();
        }

        void ShowPreview(Vector3 center, Vector3 axis, float wallThickness)
        {
            if (previewMaterial == null)
                return;
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
                filter.sharedMesh = FluidTank.HoleEdgeMesh(holeRadius, wallThickness * 1.2f, 0);
                previewRadius = holeRadius;
            }
            preview.transform.SetPositionAndRotation(center, Quaternion.LookRotation(axis));
            preview.SetActive(true);
        }

        void HidePreview()
        {
            if (preview != null)
                preview.SetActive(false);
        }

        void OnDestroy()
        {
            if (preview != null)
            {
                Destroy(preview.GetComponent<MeshFilter>().sharedMesh);
                Destroy(preview);
            }
        }
    }
}
