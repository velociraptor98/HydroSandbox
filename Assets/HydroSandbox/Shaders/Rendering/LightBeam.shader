// A light shaft through rain and spray: an additive cone (apex at the mesh origin, opening
// along +Z) that fades along its length and toward its silhouette, and softly where it passes
// into geometry or the water.
Shader "HydroSandbox/LightBeam"
{
    Properties
    {
        [HDR] _Color ("Color", Color) = (1, 0.85, 0.6, 1)
        _Length ("Length", Float) = 60
        _Falloff ("Falloff Along Length", Range(0.2, 6)) = 1.6
        _EdgeSoftness ("Edge Softness", Range(0.5, 8)) = 2.5
        _DepthFade ("Depth Fade Distance", Float) = 1.5
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }
        Blend One One
        ZWrite Off
        Cull Off

        Pass
        {
            Name "Beam"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            CBUFFER_START(UnityPerMaterial)
            float4 _Color;
            float _Length;
            float _Falloff;
            float _EdgeSoftness;
            float _DepthFade;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float along : TEXCOORD2;
                float4 screen : TEXCOORD3;
                float fogFactor : TEXCOORD4;
            };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                o.along = saturate(v.positionOS.z / _Length);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.screen = ComputeScreenPos(o.positionCS);
                o.fogFactor = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            float4 Frag(Varyings i) : SV_Target
            {
                float3 v = normalize(_WorldSpaceCameraPos - i.positionWS);
                // Seen side-on the cone's middle is thickest; its silhouette is grazing.
                float facing = pow(abs(dot(normalize(i.normalWS), v)), _EdgeSoftness);
                float along = pow(saturate(1.0 - i.along), _Falloff) * saturate(i.along * 40.0);
                float2 uv = i.screen.xy / i.screen.w;
                float sceneDepth = LinearEyeDepth(SampleSceneDepth(uv), _ZBufferParams);
                float fragmentDepth = LinearEyeDepth(i.positionCS.z, _ZBufferParams);
                float depthFade = saturate((sceneDepth - fragmentDepth) / _DepthFade);
                float3 color = _Color.rgb * (facing * along * depthFade);
                // Additive: fog scales the beam toward nothing rather than toward the fog color.
                color *= ComputeFogIntensity(i.fogFactor);
                return float4(color, 1);
            }
            ENDHLSL
        }
    }
}
