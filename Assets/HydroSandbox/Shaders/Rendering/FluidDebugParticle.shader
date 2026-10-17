// Camera-facing quads shaded and depth-written as spheres, one instance per particle, read
// straight from the solver's position buffer. Debug view only.
Shader "HydroSandbox/FluidDebugParticle"
{
    Properties
    {
        _SlowColor ("Slow Color", Color) = (0.08, 0.32, 0.85, 1)
        _FastColor ("Fast Color", Color) = (0.85, 0.95, 1, 1)
        _FastSpeed ("Speed For Fast Color", Float) = 4
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        Pass
        {
            Name "ForwardDebugParticle"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            StructuredBuffer<float4> _Positions;
            StructuredBuffer<float4> _Velocities;
            float _ParticleRadius;

            CBUFFER_START(UnityPerMaterial)
                half4 _SlowColor;
                half4 _FastColor;
                float _FastSpeed;
            CBUFFER_END

            static const float2 kCorners[6] =
            {
                float2(-1, -1), float2(1, -1), float2(1, 1),
                float2(-1, -1), float2(1, 1), float2(-1, 1)
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 corner : TEXCOORD0;
                float3 centerVS : TEXCOORD1;
                half3 color : TEXCOORD2;
            };

            struct FragOutput
            {
                half4 color : SV_Target;
                float depth : SV_Depth;
            };

            Varyings Vert(uint vertexId : SV_VertexID, uint instanceId : SV_InstanceID)
            {
                Varyings o;
                float3 centerVS = TransformWorldToView(_Positions[instanceId].xyz);
                float2 corner = kCorners[vertexId];
                o.positionCS = TransformWViewToHClip(centerVS + float3(corner * _ParticleRadius, 0));
                o.corner = corner;
                o.centerVS = centerVS;
                float speed = length(_Velocities[instanceId].xyz);
                o.color = lerp(_SlowColor.rgb, _FastColor.rgb, saturate(speed / _FastSpeed));
                return o;
            }

            FragOutput Frag(Varyings i)
            {
                float r2 = dot(i.corner, i.corner);
                clip(1.0 - r2);

                // Unity view space looks down -Z, so the camera-facing hemisphere is +Z.
                float3 normalVS = float3(i.corner, sqrt(1.0 - r2));
                float4 surfaceCS = TransformWViewToHClip(i.centerVS + normalVS * _ParticleRadius);

                Light light = GetMainLight();
                float3 lightVS = TransformWorldToViewDir(light.direction);
                half diffuse = saturate(dot(normalVS, lightVS));

                FragOutput o;
                o.color = half4(i.color * (0.3 + 0.7 * diffuse * light.color), 1);
                o.depth = surfaceCS.z / surfaceCS.w;
                return o;
            }
            ENDHLSL
        }
    }
}
