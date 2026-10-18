using UnityEngine;

namespace HydroSandbox
{
    /// <summary>
    /// A clickable nozzle (this object needs a collider): clicking it with <see cref="TankTools"/>
    /// switches its <see cref="FluidSource"/> on or off. The indicator glows in the liquid's color
    /// while pouring, and faintly while hovered.
    /// </summary>
    public class PourNozzle : MonoBehaviour
    {
        static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");

        [SerializeField] FluidSource source;
        [Tooltip("Lit in the liquid's color while pouring; its material needs emission enabled.")]
        [SerializeField] Renderer indicator;
        [SerializeField] Color glow = Color.white;

        MaterialPropertyBlock block;
        Color shown = new Color(-1f, 0f, 0f);

        public FluidSource Source => source;
        public bool Hovered { get; set; }
        public bool Pouring => source != null && source.enabled;

        public void Configure(FluidSource nozzleSource, Renderer nozzleIndicator, Color glowColor)
        {
            source = nozzleSource;
            indicator = nozzleIndicator;
            glow = glowColor;
        }

        public void Toggle()
        {
            if (source != null)
                source.enabled = !source.enabled;
        }

        void Update()
        {
            if (indicator == null)
                return;
            Color target = Pouring ? glow * 2.5f : Hovered ? glow * 0.6f : Color.black;
            if (target == shown)
                return;
            shown = target;
            block ??= new MaterialPropertyBlock();
            indicator.GetPropertyBlock(block);
            block.SetColor(EmissionColor, target);
            indicator.SetPropertyBlock(block);
        }
    }
}
