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
        const int WhitewaterSplatPass = 4;
        const int WhitewaterCompositePass = 5;
        const int OpticsPass = 6;
        const float NoFluidDepth = 1e6f;

        static readonly int SceneDepthId = Shader.PropertyToID("_SceneDepth");
        static readonly int SceneColorId = Shader.PropertyToID("_SceneColor");
        static readonly int FluidDepthId = Shader.PropertyToID("_FluidDepth");
        static readonly int FluidThicknessId = Shader.PropertyToID("_FluidThickness");
        static readonly int FluidAbsorptionId = Shader.PropertyToID("_FluidAbsorption");
        static readonly int FluidScatteringId = Shader.PropertyToID("_FluidScattering");
        static readonly int BlurSourceId = Shader.PropertyToID("_BlurSource");
        static readonly int BlurDirectionId = Shader.PropertyToID("_BlurDirection");
        static readonly int BlurSpreadId = Shader.PropertyToID("_BlurSpread");
        static readonly int WhitewaterId = Shader.PropertyToID("_Whitewater");

        readonly MaterialPropertyBlock properties = new MaterialPropertyBlock();

        class PassData
        {
            internal FluidSurface surface;
            internal Material material;
            internal MaterialPropertyBlock properties;
            internal int instanceCount;
            internal int dropletCount;
            internal TextureHandle sceneDepth;
            internal TextureHandle sceneColor;
            internal TextureHandle fluidDepth;
            internal TextureHandle fluidThickness;
            internal TextureHandle fluidAbsorption;
            internal TextureHandle fluidScattering;
            internal bool mixed;
            internal TextureHandle blurSource;
            internal TextureHandle whitewater;
            internal Vector2 blurDirection;
            internal bool blurSpread;
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
            TextureHandle thickness = CreateColor(renderGraph, width, height, GraphicsFormat.R16G16_SFloat, "_FluidThickness", Color.clear);
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

            bool mixed = surface.HasMixedLiquids;
            TextureHandle absorption = TextureHandle.nullHandle;
            TextureHandle scattering = TextureHandle.nullHandle;
            if (mixed)
            {
                absorption = CreateColor(renderGraph, width, height, GraphicsFormat.R16G16B16A16_SFloat, "_FluidAbsorption", Color.clear);
                scattering = CreateColor(renderGraph, width, height, GraphicsFormat.R16G16B16A16_SFloat, "_FluidScattering", Color.clear);
                using var builder = renderGraph.AddRasterRenderPass<PassData>("Fluid Optics", out var data, profilingSampler);
                Fill(data, surface, material, instanceCount);
                data.sceneDepth = resources.cameraDepthTexture;
                builder.UseTexture(data.sceneDepth, AccessFlags.Read);
                builder.SetRenderAttachment(absorption, 0, AccessFlags.Write);
                builder.SetRenderAttachment(scattering, 1, AccessFlags.Write);
                builder.SetRenderFunc(static (PassData d, RasterGraphContext ctx) =>
                {
                    d.properties.Clear();
                    d.surface.SetOpticsProperties(d.properties);
                    d.properties.SetTexture(SceneDepthId, d.sceneDepth);
                    ctx.cmd.DrawProcedural(Matrix4x4.identity, d.material, OpticsPass, MeshTopology.Triangles, 6, d.instanceCount, d.properties);
                });
            }

            TextureHandle blurInput = fluidDepth;
            for (int i = 0; i < surface.BlurIterations; i++)
            {
                bool spread = i > 0;
                AddBlurPass(renderGraph, surface, material, blurInput, blurTemp, new Vector2(1f / width, 0f), spread, "Fluid Depth Blur H");
                AddBlurPass(renderGraph, surface, material, blurTemp, blurred, new Vector2(0f, 1f / height), spread, "Fluid Depth Blur V");
                blurInput = blurred;
            }

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Fluid Composite", out var data, profilingSampler))
            {
                Fill(data, surface, material, instanceCount);
                data.sceneDepth = resources.cameraDepthTexture;
                data.sceneColor = resources.cameraOpaqueTexture;
                data.fluidDepth = blurred;
                data.fluidThickness = thickness;
                data.mixed = mixed;
                data.fluidAbsorption = absorption;
                data.fluidScattering = scattering;
                if (mixed)
                {
                    builder.UseTexture(absorption, AccessFlags.Read);
                    builder.UseTexture(scattering, AccessFlags.Read);
                }
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
                    if (d.mixed)
                    {
                        d.properties.SetTexture(FluidAbsorptionId, d.fluidAbsorption);
                        d.properties.SetTexture(FluidScatteringId, d.fluidScattering);
                    }
                    else
                    {
                        d.properties.SetTexture(FluidAbsorptionId, Texture2D.blackTexture);
                        d.properties.SetTexture(FluidScatteringId, Texture2D.blackTexture);
                    }
                    ctx.cmd.DrawProcedural(Matrix4x4.identity, d.material, CompositePass, MeshTopology.Triangles, 3, 1, d.properties);
                });
            }

            RecordWhitewater(renderGraph, resources, surface, material, blurred, width, height);
        }

        void RecordWhitewater(RenderGraph renderGraph, UniversalResourceData resources, FluidSurface surface,
            Material material, TextureHandle fluidDepth, int width, int height)
        {
            TextureHandle whitewater = CreateColor(renderGraph, width, height, GraphicsFormat.R16G16_SFloat, "_Whitewater", Color.clear);

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Whitewater Splats", out var data, profilingSampler))
            {
                Fill(data, surface, material, surface.Whitewater != null ? surface.Whitewater.Capacity : 0);
                data.dropletCount = surface.Solver.Capacity;
                data.sceneDepth = resources.cameraDepthTexture;
                data.fluidDepth = fluidDepth;
                builder.UseTexture(data.sceneDepth, AccessFlags.Read);
                builder.UseTexture(data.fluidDepth, AccessFlags.Read);
                builder.SetRenderAttachment(whitewater, 0, AccessFlags.Write);
                builder.SetRenderFunc(static (PassData d, RasterGraphContext ctx) =>
                {
                    if (d.instanceCount > 0)
                    {
                        d.properties.Clear();
                        d.surface.SetWhitewaterSplatProperties(d.properties, false);
                        d.properties.SetTexture(SceneDepthId, d.sceneDepth);
                        d.properties.SetTexture(FluidDepthId, d.fluidDepth);
                        ctx.cmd.DrawProcedural(Matrix4x4.identity, d.material, WhitewaterSplatPass, MeshTopology.Triangles, 6, d.instanceCount, d.properties);
                    }
                    d.properties.Clear();
                    d.surface.SetWhitewaterSplatProperties(d.properties, true);
                    d.properties.SetTexture(SceneDepthId, d.sceneDepth);
                    d.properties.SetTexture(FluidDepthId, d.fluidDepth);
                    ctx.cmd.DrawProcedural(Matrix4x4.identity, d.material, WhitewaterSplatPass, MeshTopology.Triangles, 6, d.dropletCount, d.properties);
                });
            }

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("Whitewater Composite", out var data, profilingSampler))
            {
                Fill(data, surface, material, 0);
                data.whitewater = whitewater;
                data.sceneDepth = resources.cameraDepthTexture;
                data.fluidDepth = fluidDepth;
                builder.UseTexture(data.whitewater, AccessFlags.Read);
                builder.UseTexture(data.sceneDepth, AccessFlags.Read);
                builder.UseTexture(data.fluidDepth, AccessFlags.Read);
                builder.SetRenderAttachment(resources.activeColorTexture, 0, AccessFlags.ReadWrite);
                builder.SetRenderFunc(static (PassData d, RasterGraphContext ctx) =>
                {
                    d.properties.Clear();
                    d.surface.SetWhitewaterCompositeProperties(d.properties);
                    d.properties.SetTexture(WhitewaterId, d.whitewater);
                    d.properties.SetTexture(SceneDepthId, d.sceneDepth);
                    d.properties.SetTexture(FluidDepthId, d.fluidDepth);
                    ctx.cmd.DrawProcedural(Matrix4x4.identity, d.material, WhitewaterCompositePass, MeshTopology.Triangles, 3, 1, d.properties);
                });
            }
        }

        void AddBlurPass(RenderGraph renderGraph, FluidSurface surface, Material material, TextureHandle source,
            TextureHandle destination, Vector2 direction, bool spread, string name)
        {
            using var builder = renderGraph.AddRasterRenderPass<PassData>(name, out var data, profilingSampler);
            Fill(data, surface, material, 0);
            data.blurSource = source;
            data.blurDirection = direction;
            data.blurSpread = spread;
            builder.UseTexture(source, AccessFlags.Read);
            builder.SetRenderAttachment(destination, 0, AccessFlags.Write);
            builder.SetRenderFunc(static (PassData d, RasterGraphContext ctx) =>
            {
                d.properties.Clear();
                d.surface.SetBlurProperties(d.properties);
                d.properties.SetTexture(BlurSourceId, d.blurSource);
                d.properties.SetVector(BlurDirectionId, d.blurDirection);
                d.properties.SetFloat(BlurSpreadId, d.blurSpread ? 1f : 0f);
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
