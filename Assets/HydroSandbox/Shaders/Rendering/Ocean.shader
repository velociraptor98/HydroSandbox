// The FFT ocean surface (OceanWaves) on a camera-following grid (OceanSurface). Opaque, so the
// particle water's screen-space pass refracts it like any other scene geometry. Inside a
// particle zone the heightfield sinks across the edge band and is cut away beyond it, leaving
// the particles as the only water there.
Shader "HydroSandbox/Ocean"
{
    Properties
    {
        _DeepColor ("Deep Color", Color) = (0.012, 0.03, 0.036, 1)
        _ScatterColor ("Scatter Color", Color) = (0.05, 0.17, 0.16, 1)
        _FoamColor ("Foam Color", Color) = (0.85, 0.88, 0.9, 1)
        _FoamTexture ("Foam Pattern", 2D) = "white" {}
        _FoamScale ("Foam Pattern Repeats Per Meter", Float) = 0.12
        _DetailNormal ("Detail Normal", 2D) = "bump" {}
        _DetailScale ("Detail Repeats Per Meter", Float) = 0.6
        _DetailStrength ("Detail Strength", Range(0, 1)) = 0.25
        _Roughness ("Roughness", Range(0.01, 1)) = 0.14
        _SubsurfaceStrength ("Subsurface Strength", Range(0, 4)) = 1.2
        _CrestHeight ("Crest Height For Full Scattering", Float) = 2.5
        _LodPerMeter ("Vertex Spacing Per Meter Of Distance", Float) = 0.012
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "Queue" = "Geometry" "RenderPipeline" = "UniversalPipeline" }

        HLSLINCLUDE
        #pragma target 4.5
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Assets/HydroSandbox/Shaders/Compute/OceanCommon.hlsl"

        CBUFFER_START(UnityPerMaterial)
        float4 _DeepColor;
        float4 _ScatterColor;
        float4 _FoamColor;
        float4 _FoamTexture_ST;
        float _FoamScale;
        float4 _DetailNormal_ST;
        float _DetailScale;
        float _DetailStrength;
        float _Roughness;
        float _SubsurfaceStrength;
        float _CrestHeight;
        float _LodPerMeter;
        CBUFFER_END

        struct Attributes
        {
            float4 positionOS : POSITION;
        };

        struct Varyings
        {
            float4 positionCS : SV_POSITION;
            float3 positionWS : TEXCOORD0;
            float2 restXZ : TEXCOORD1;
            float zoneDistance : TEXCOORD2;
            float height : TEXCOORD3;
            float fogFactor : TEXCOORD4;
        };

        Varyings Vert(Attributes input)
        {
            Varyings o;
            float3 rest = TransformObjectToWorld(input.positionOS.xyz);
            // Sample coarser mips as the grid coarsens with distance, or far waves alias.
            float spacing = max(distance(rest.xz, _WorldSpaceCameraPos.xz) * _LodPerMeter, 1e-3);
            float lod0 = max(0.0, log2(spacing * 256.0 * _OceanCascadeSizes.z));
            float lod1 = max(0.0, log2(spacing * 256.0 * _OceanCascadeSizes.w));
            float3 displacement = OceanDisplacement(rest.xz, lod0, lod1) * OceanShelter(rest.xz);
            float3 positionWS = rest + displacement;

            float zone = _OceanZoneParams.z > 0.0 ? OceanZoneDistance(positionWS.xz) : -1.0;
            if (_OceanZoneParams.z > 0.0)
                positionWS.y -= _OceanZoneParams.y * smoothstep(_OceanZoneParams.w, _OceanZoneParams.x, zone);
            float2 tsunamiSlope;
            float tsunami = TsunamiHeight(positionWS.xz, tsunamiSlope);
            positionWS.y += tsunami;

            o.positionWS = positionWS;
            o.restXZ = rest.xz;
            o.zoneDistance = zone;
            o.height = displacement.y + tsunami;
            o.positionCS = TransformWorldToHClip(positionWS);
            o.fogFactor = ComputeFogFactor(o.positionCS.z);
            return o;
        }

        void ClipToZone(Varyings i)
        {
            if (_OceanZoneParams.z > 0.0 && i.zoneDistance > _OceanZoneParams.x)
                discard;
        }

        TEXTURE2D(_OceanDerivatives0);
        TEXTURE2D(_OceanDerivatives1);
        TEXTURE2D(_FoamTexture);
        SAMPLER(sampler_FoamTexture);
        TEXTURE2D(_DetailNormal);
        SAMPLER(sampler_DetailNormal);

        float3 OceanNormal(float2 restXZ, float3 positionWS)
        {
            float2 tsunamiSlope;
            TsunamiHeight(positionWS.xz, tsunamiSlope);
            float4 d = SAMPLE_TEXTURE2D(_OceanDerivatives0, sampler_ocean_trilinear_repeat, restXZ * _OceanCascadeSizes.z)
                     + SAMPLE_TEXTURE2D(_OceanDerivatives1, sampler_ocean_trilinear_repeat, restXZ * _OceanCascadeSizes.w);
            // Slope of the displaced surface: the height gradient over the stretch of the grid.
            float2 slope = float2(d.x / (1.0 + d.z), d.y / (1.0 + d.w)) + tsunamiSlope;
            float3 n = normalize(float3(-slope.x, 1.0, -slope.y));

            float2 uv = positionWS.xz * _DetailScale;
            float t = _Time.y * 0.03;
            float3 detail = UnpackNormal(SAMPLE_TEXTURE2D(_DetailNormal, sampler_DetailNormal, uv + float2(t, t * 0.7)))
                          + UnpackNormal(SAMPLE_TEXTURE2D(_DetailNormal, sampler_DetailNormal, uv * 2.3 - float2(t * 0.6, -t)));
            float fade = saturate(1.0 - distance(positionWS, _WorldSpaceCameraPos) / 150.0);
            return normalize(n + float3(detail.x, 0, detail.y) * (_DetailStrength * fade));
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Assets/HydroSandbox/Shaders/Rendering/OceanFoam.hlsl"

            float4 Frag(Varyings i) : SV_Target
            {
                ClipToZone(i);
                float3 positionWS = i.positionWS;
                float3 toCamera = _WorldSpaceCameraPos - positionWS;
                float distanceToCamera = length(toCamera);
                float3 v = toCamera / distanceToCamera;
                float3 n = OceanNormal(i.restXZ, positionWS);
                // Far away the normal map is below a pixel; flatten it rather than sparkle.
                n = normalize(lerp(n, float3(0, 1, 0), saturate(distanceToCamera / 3000.0)));
                float nDotV = saturate(dot(n, v));

                Light mainLight = GetMainLight();
                float3 ambient = SampleSH(float3(0, 1, 0));

                // Reflection of the sky. Rays reflected below the horizon would see the sky
                // probe's empty lower half; fold them up.
                float3 r = reflect(-v, n);
                r.y = abs(r.y);
                float perceptualRoughness = saturate(_Roughness + distanceToCamera / 4000.0);
                float4 encoded = SAMPLE_TEXTURECUBE_LOD(_GlossyEnvironmentCubeMap, sampler_GlossyEnvironmentCubeMap, r,
                    PerceptualRoughnessToMipmapLevel(perceptualRoughness));
                float3 reflection = DecodeHDREnvironment(encoded, _GlossyEnvironmentCubeMap_HDR);
                float fresnel = 0.02 + 0.98 * pow(1.0 - nDotV, 5.0);

                // Light scattered back out of the water: dark in the troughs, brightest in the thin
                // tops of crests, most of all looking toward the light through them.
                float crest = saturate(i.height / _CrestHeight);
                float through = pow(saturate(dot(v, -mainLight.direction) * 0.5 + 0.5), 4.0);
                float3 body = _DeepColor.rgb * ambient
                            + _ScatterColor.rgb * (ambient * (0.3 + crest) + mainLight.color * (crest * crest * through * _SubsurfaceStrength));

                float3 color = lerp(body, reflection, fresnel);

                float roughness = max(perceptualRoughness * perceptualRoughness, 0.02);
                float3 h = normalize(mainLight.direction + v);
                float nDotH = saturate(dot(n, h));
                float a2 = roughness * roughness;
                float denom = nDotH * nDotH * (a2 - 1.0) + 1.0;
                float ggx = a2 / (PI * denom * denom);
                color += mainLight.color * (ggx * fresnel * saturate(dot(n, mainLight.direction)));

                float foam = OceanFoamCoverage(i.restXZ, TEXTURE2D_ARGS(_FoamTexture, sampler_FoamTexture), _FoamScale);
                // A tsunami's crest breaks white along its top.
                float2 unusedSlope;
                float tsunami = TsunamiHeight(positionWS.xz, unusedSlope);
                if (_Tsunami0.w > 0.0)
                    foam = max(foam, smoothstep(0.6, 0.97, tsunami / _Tsunami0.w) * (0.55 + 0.45 * SAMPLE_TEXTURE2D(_FoamTexture, sampler_FoamTexture, i.restXZ * _FoamScale * 2.0).r));
                color = lerp(color, OceanFoamLit(_FoamColor.rgb, ambient, n, mainLight), foam);

                color = MixFog(color, i.fogFactor);
                return float4(color, 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            float4 Frag(Varyings i) : SV_Target
            {
                ClipToZone(i);
                return i.positionCS.z;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            float4 Frag(Varyings i) : SV_Target
            {
                ClipToZone(i);
                return float4(OceanNormal(i.restXZ, i.positionWS), 0.0);
            }
            ENDHLSL
        }
    }
}
