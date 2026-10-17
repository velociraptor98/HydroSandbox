using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace HydroSandbox.Rendering
{
    /// <summary>
    /// Renders every active <see cref="FluidSurface"/> as a continuous screen-space water surface.
    /// Add it to the URP renderer that should show fluid.
    /// </summary>
    public class FluidSurfaceFeature : ScriptableRendererFeature
    {
        [SerializeField] RenderPassEvent passEvent = RenderPassEvent.BeforeRenderingTransparents;

        FluidSurfacePass pass;

        public override void Create()
        {
            pass = new FluidSurfacePass { renderPassEvent = passEvent };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (FluidSurface.Active.Count == 0)
                return;
            CameraType cameraType = renderingData.cameraData.cameraType;
            if (cameraType == CameraType.Preview || cameraType == CameraType.Reflection)
                return;

            // Refraction reads the opaque color copy; occlusion and thickness read scene depth.
            pass.ConfigureInput(ScriptableRenderPassInput.Color | ScriptableRenderPassInput.Depth);
            renderer.EnqueuePass(pass);
        }
    }

    class FluidSurfacePass : ScriptableRenderPass
    {
        const int DepthPass = 0;
        const int ThicknessPass = 1;
        const int BlurPass = 2;
        const int CompositePass = 3;
        const float NoFluidDepth = 1e6f;

        static readonly int SceneDepthId = Shader.PropertyToID("_SceneDepth");
        static readonly int SceneColorId = Shader.PropertyToID("_SceneColor");
        static readonly int FluidDepthId = Shader.PropertyToID("_FluidDepth");
        static readonly int FluidThicknessId = Shader.PropertyToID("_FluidThickness");
        static readonly int BlurSourceId = Shader.PropertyToID("_BlurSource");
        static readonly int BlurDirectionId = Shader.PropertyToID("_BlurDirection");

        readonly MaterialPropertyBlock properties = new MaterialPropertyBlock();

        class PassData
        {
            internal FluidSurface surface;
            internal Material material;
            internal MaterialPropertyBlock properties;
            internal int instanceCount;
            internal TextureHandle sceneDepth;
            internal TextureHandle sceneColor;
            internal TextureHandle fluidDepth;
            internal TextureHandle fluidThickness;
            internal TextureHandle blurSource;
            internal Vector2 blurDirection;
        }

        public FluidSurfacePass()
        {
            profilingSampler = new ProfilingSampler("HydroSandbox Fluid Surface");
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resources = frameData.Get<UniversalResourceData>();
            if (!resources.cameraDepthTexture.IsValid() || !resources.cameraOpaqueTexture.IsValid())
                return;

            foreach (FluidSurface surface in FluidSurface.Active)
            {
                if (surface.IsRenderable && surface.Material != null)
                    RecordSurface(renderGraph, resources, surface);
            }
        }

        void RecordSurface(RenderGraph renderGraph, UniversalResourceData resources, FluidSurface surface)
        {
            TextureDesc colorDesc = renderGraph.GetTextureDesc(resources.activeColorTexture);
            int width = colorDesc.width;
            int height = colorDesc.height;

            TextureHandle fluidDepth = CreateColor(renderGraph, width, height, GraphicsFormat.R32_SFloat, "_FluidDepth",
                new Color(NoFluidDepth, 0, 0, 0));
            TextureHandle blurTemp = CreateColor(renderGraph, width, height, GraphicsFormat.R32_SFloat, "_FluidDepthBlurH", null);
            TextureHandle blurred = CreateColor(renderGraph, width, height, GraphicsFormat.R32_SFloat, "_FluidDepthBlurred", null);
            TextureHandle thickness = CreateColor(renderGraph, width, height, GraphicsFormat.R16G16B16A16_SFloat, "_FluidThickness", Color.clear);
            var zDesc = new TextureDesc(width, height)
            {
                depthBufferBits = DepthBits.Depth32,
                clearBuffer = true,
                name = "_FluidZ",
            };
            TextureHandle fluidZ = renderGraph.CreateTexture(zDesc);

            Material material = surface.Material;
            int instanceCount = surface.Solver.Capacity;

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Fluid Particle Depth", out var data, profilingSampler))
            {
                Fill(data, surface, material, instanceCount);
                builder.SetRenderAttachment(fluidDepth, 0, AccessFlags.Write);
                builder.SetRenderAttachmentDepth(fluidZ, AccessFlags.Write);
                builder.AllowPassCulling(false);
                builder.SetRenderFunc(static (PassData d, RasterGraphContext ctx) =>
                {
                    d.properties.Clear();
                    d.surface.SetParticleProperties(d.properties);
                    ctx.cmd.DrawProcedural(Matrix4x4.identity, d.material, DepthPass, MeshTopology.Triangles, 6, d.instanceCount, d.properties);
                });
            }

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Fluid Thickness", out var data, profilingSampler))
            {
                Fill(data, surface, material, instanceCount);
                data.sceneDepth = resources.cameraDepthTexture;
                data.fluidDepth = fluidDepth;
                builder.UseTexture(data.sceneDepth, AccessFlags.Read);
                builder.UseTexture(data.fluidDepth, AccessFlags.Read);
                builder.SetRenderAttachment(thickness, 0, AccessFlags.Write);
                builder.SetRenderFunc(static (PassData d, RasterGraphContext ctx) =>
                {
                    d.properties.Clear();
                    d.surface.SetParticleProperties(d.properties);
                    d.properties.SetTexture(SceneDepthId, d.sceneDepth);
                    d.properties.SetTexture(FluidDepthId, d.fluidDepth);
                    ctx.cmd.DrawProcedural(Matrix4x4.identity, d.material, ThicknessPass, MeshTopology.Triangles, 6, d.instanceCount, d.properties);
                });
            }

            TextureHandle blurInput = fluidDepth;
            for (int i = 0; i < surface.BlurIterations; i++)
            {
                AddBlurPass(renderGraph, surface, material, blurInput, blurTemp, new Vector2(1f / width, 0f), "Fluid Depth Blur H");
                AddBlurPass(renderGraph, surface, material, blurTemp, blurred, new Vector2(0f, 1f / height), "Fluid Depth Blur V");
                blurInput = blurred;
            }

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Fluid Composite", out var data, profilingSampler))
            {
                Fill(data, surface, material, instanceCount);
                data.sceneDepth = resources.cameraDepthTexture;
                data.sceneColor = resources.cameraOpaqueTexture;
                data.fluidDepth = blurred;
                data.fluidThickness = thickness;
                builder.UseTexture(data.sceneDepth, AccessFlags.Read);
                builder.UseTexture(data.sceneColor, AccessFlags.Read);
                builder.UseTexture(data.fluidDepth, AccessFlags.Read);
                builder.UseTexture(data.fluidThickness, AccessFlags.Read);
                builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);
                builder.SetRenderFunc(static (PassData d, RasterGraphContext ctx) =>
                {
                    d.properties.Clear();
                    d.surface.SetCompositeProperties(d.properties);
                    d.properties.SetTexture(SceneDepthId, d.sceneDepth);
                    d.properties.SetTexture(SceneColorId, d.sceneColor);
                    d.properties.SetTexture(FluidDepthId, d.fluidDepth);
                    d.properties.SetTexture(FluidThicknessId, d.fluidThickness);
                    ctx.cmd.DrawProcedural(Matrix4x4.identity, d.material, CompositePass, MeshTopology.Triangles, 3, 1, d.properties);
                });
            }
        }

        void AddBlurPass(RenderGraph renderGraph, FluidSurface surface, Material material, TextureHandle source,
            TextureHandle destination, Vector2 direction, string name)
        {
            using var builder = renderGraph.AddRasterRenderPass<PassData>(name, out var data, profilingSampler);
            Fill(data, surface, material, 0);
            data.blurSource = source;
            data.blurDirection = direction;
            builder.UseTexture(source, AccessFlags.Read);
            builder.SetRenderAttachment(destination, 0, AccessFlags.Write);
            builder.SetRenderFunc(static (PassData d, RasterGraphContext ctx) =>
            {
                d.properties.Clear();
                d.surface.SetBlurProperties(d.properties);
                d.properties.SetTexture(BlurSourceId, d.blurSource);
                d.properties.SetVector(BlurDirectionId, d.blurDirection);
                ctx.cmd.DrawProcedural(Matrix4x4.identity, d.material, BlurPass, MeshTopology.Triangles, 3, 1, d.properties);
            });
        }

        void Fill(PassData data, FluidSurface surface, Material material, int instanceCount)
        {
            data.surface = surface;
            data.material = material;
            data.properties = properties;
            data.instanceCount = instanceCount;
        }

        static TextureHandle CreateColor(RenderGraph renderGraph, int width, int height, GraphicsFormat format,
            string name, Color? clearColor)
        {
            var desc = new TextureDesc(width, height)
            {
                colorFormat = format,
                name = name,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                clearBuffer = clearColor.HasValue,
                clearColor = clearColor ?? Color.clear,
            };
            return renderGraph.CreateTexture(desc);
        }
    }
}
