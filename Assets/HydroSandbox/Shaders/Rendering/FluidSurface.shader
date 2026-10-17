// Screen-space fluid rendering (van der Laan et al. 2009 style), driven by FluidSurfaceFeature:
//   0 ParticleDepth      sphere impostors -> nearest linear eye depth
//   1 ParticleThickness  additive chord length (R), near-surface aeration (G), all aeration (B),
//                        speed-weighted chord (A)
//   2 BlurDepth          separable bilateral filter on eye depth, run once per axis
//   3 Composite          normals from depth, refraction, Fresnel reflection, absorption, foam
Shader "HydroSandbox/FluidSurface"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off
        Cull Off

        HLSLINCLUDE
        #pragma target 4.5
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

        #define NO_FLUID_DEPTH 1e6

        StructuredBuffer<float4> _Positions;
        StructuredBuffer<float4> _Velocities;
        float _ParticleRadius;
        float _DepthRadiusScale;
        float _ThicknessScale;
        float _FoamDepthFalloff;

        TEXTURE2D(_SceneDepth);
        TEXTURE2D(_SceneColor);
        TEXTURE2D(_FluidDepth);
        TEXTURE2D(_FluidThickness);
        TEXTURE2D(_BlurSource);

        float SceneEyeDepth(float2 uv)
        {
            return LinearEyeDepth(SAMPLE_TEXTURE2D_LOD(_SceneDepth, sampler_PointClamp, uv, 0).r, _ZBufferParams);
        }

        float3 WorldPositionFromEyeDepth(float2 uv, float eyeDepth)
        {
            float deviceDepth = (rcp(eyeDepth) - _ZBufferParams.w) / _ZBufferParams.z;
            return ComputeWorldSpacePosition(uv, deviceDepth, UNITY_MATRIX_I_VP);
        }

        // ---- Particle impostors -------------------------------------------------------------

        static const float2 kCorners[6] =
        {
            float2(-1, -1), float2(1, -1), float2(1, 1),
            float2(-1, -1), float2(1, 1), float2(-1, 1)
        };

        struct ParticleVaryings
        {
            float4 positionCS : SV_POSITION;
            float2 corner : TEXCOORD0;
            float3 centerVS : TEXCOORD1;
            float radius : TEXCOORD2;
            float aeration : TEXCOORD3;
            float speed : TEXCOORD4;
        };

        ParticleVaryings ParticleVert(uint vertexId, uint instanceId, float radiusScale)
        {
            ParticleVaryings o;
            float4 particle = _Positions[instanceId];
            float radius = _ParticleRadius * radiusScale;
            float3 centerVS = TransformWorldToView(particle.xyz);
            float2 corner = kCorners[vertexId];
            o.positionCS = TransformWViewToHClip(centerVS + float3(corner * radius, 0));
            o.corner = corner;
            o.centerVS = centerVS;
            o.radius = radius;
            float4 velocity = _Velocities[instanceId];
            o.aeration = velocity.w;
            o.speed = length(velocity.xyz);
            // Dead pool slots (w = 0) collapse to a degenerate quad outside the clip volume.
            if (particle.w < 0.5)
                o.positionCS = float4(2, 2, 2, 1);
            return o;
        }

        // ---- Full-screen triangle -----------------------------------------------------------

        struct FullscreenVaryings
        {
            float4 positionCS : SV_POSITION;
        };

        FullscreenVaryings FullscreenVert(uint vertexId : SV_VertexID)
        {
            FullscreenVaryings o;
            o.positionCS = GetFullScreenTriangleVertexPosition(vertexId);
            return o;
        }
        ENDHLSL

        Pass
        {
            Name "ParticleDepth"
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            ParticleVaryings Vert(uint vertexId : SV_VertexID, uint instanceId : SV_InstanceID)
            {
                return ParticleVert(vertexId, instanceId, _DepthRadiusScale);
            }

            float Frag(ParticleVaryings i, out float depth : SV_Depth) : SV_Target
            {
                float r2 = dot(i.corner, i.corner);
                clip(1.0 - r2);
                float3 surfaceVS = i.centerVS + float3(i.corner, sqrt(1.0 - r2)) * i.radius;
                float4 surfaceCS = TransformWViewToHClip(surfaceVS);
                depth = surfaceCS.z / surfaceCS.w;
                return -surfaceVS.z;
            }
            ENDHLSL
        }

        Pass
        {
            Name "ParticleThickness"
            ZTest Always
            Blend One One

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            ParticleVaryings Vert(uint vertexId : SV_VertexID, uint instanceId : SV_InstanceID)
            {
                return ParticleVert(vertexId, instanceId, 1.0);
            }

            float4 Frag(ParticleVaryings i) : SV_Target
            {
                float r2 = dot(i.corner, i.corner);
                clip(1.0 - r2);
                float z = sqrt(1.0 - r2);
                // Particles behind scene geometry must not add thickness to what is in front.
                float frontEyeDepth = -(i.centerVS.z + z * i.radius);
                float2 uv = GetNormalizedScreenSpaceUV(i.positionCS);
                clip(SceneEyeDepth(uv) - frontEyeDepth);
                float chord = 2.0 * z * i.radius * _ThicknessScale;
                // Foam is what floats at the visible surface, so aeration only counts within a
                // few particle radii of the front depth.
                float frontSurface = SAMPLE_TEXTURE2D_LOD(_FluidDepth, sampler_PointClamp, uv, 0).r;
                float surfaceWeight = exp(-max(frontEyeDepth - frontSurface, 0.0) / _FoamDepthFalloff);
                return float4(chord, chord * i.aeration * surfaceWeight, chord * i.aeration, chord * i.speed);
            }
            ENDHLSL
        }

        Pass
        {
            Name "BlurDepth"
            ZTest Always

            HLSLPROGRAM
            #pragma vertex FullscreenVert
            #pragma fragment Frag

            float2 _BlurDirection;      // one texel along the blur axis, in UV
            float _BlurWorldRadius;     // filter radius in world units, projected per pixel
            float _BlurDepthFalloff;    // deviation (m) from the local surface plane at which a tap's weight falls to e^-1
            int _BlurMaxRadius;         // pixel clamp

            float Frag(FullscreenVaryings i) : SV_Target
            {
                float2 uv = GetNormalizedScreenSpaceUV(i.positionCS);
                float center = SAMPLE_TEXTURE2D_LOD(_BlurSource, sampler_PointClamp, uv, 0).r;
                if (center >= NO_FLUID_DEPTH)
                    return center;

                // Pixels covered by the world-space radius at this depth. abs(): the projection's
                // y scale is negative when URP flips rendering into a texture.
                float pixelRadius = _BlurWorldRadius * abs(UNITY_MATRIX_P._m11) * 0.5 * _ScaledScreenParams.y / center;
                int radius = clamp((int)pixelRadius, 1, _BlurMaxRadius);
                float sigma = max(radius * 0.5, 1.0);
                float invTwoSigma2 = 0.5 / (sigma * sigma);
                float invFalloff2 = rcp(_BlurDepthFalloff * _BlurDepthFalloff);

                // Range weights compare taps against the local surface plane, not the center depth:
                // at grazing angles a flat surface changes depth quickly per pixel, and a plain
                // depth test would refuse to smooth it, leaving the particle rows as terraces. The
                // slope is the gentler one-sided difference, so silhouettes still stop the blur. It
                // is measured over half the radius so it follows the surface, not one particle's cap.
                int stride = max(radius >> 1, 1);
                float dPrev = SAMPLE_TEXTURE2D_LOD(_BlurSource, sampler_PointClamp, uv - _BlurDirection * stride, 0).r;
                float dNext = SAMPLE_TEXTURE2D_LOD(_BlurSource, sampler_PointClamp, uv + _BlurDirection * stride, 0).r;
                float slopePrev = dPrev < NO_FLUID_DEPTH ? (center - dPrev) / stride : 1e6;
                float slopeNext = dNext < NO_FLUID_DEPTH ? (dNext - center) / stride : 1e6;
                float slope = abs(slopePrev) < abs(slopeNext) ? slopePrev : slopeNext;
                slope = abs(slope * stride) < 4.0 * _BlurDepthFalloff ? slope : 0.0;

                float sum = center;
                float weightSum = 1.0;
                for (int k = -radius; k <= radius; k++)
                {
                    if (k == 0)
                        continue;
                    float d = SAMPLE_TEXTURE2D_LOD(_BlurSource, sampler_PointClamp, uv + _BlurDirection * k, 0).r;
                    if (d >= NO_FLUID_DEPTH)
                        continue;
                    float dz = d - (center + slope * k);
                    float w = exp(-k * k * invTwoSigma2 - dz * dz * invFalloff2);
                    sum += d * w;
                    weightSum += w;
                }
                return sum / weightSum;
            }
            ENDHLSL
        }

        Pass
        {
            Name "Composite"
            ZTest Always
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex FullscreenVert
            #pragma fragment Frag

            TEXTURE2D(_RippleNormal);
            TEXTURE2D(_FoamMask);
            TEXTURE2D(_FoamColor);

            float4 _AbsorptionCoefficients; // per meter, rgb
            float4 _ScatterColor;
            float4 _FoamTint;
            float _RefractionStrength;
            float _RippleStrength;
            float _RippleScale;
            float _RippleSpeed;
            float _FoamScale;
            float _FoamAmount;
            float _BubbleAmount;
            float _FlowSpeedMin;
            float _FlowSpeedRange;
            float _FlowSheen;
            float _EdgeOpacity;
            float _Smoothness;

            float3 FluidWorldPosition(float2 uv)
            {
                return WorldPositionFromEyeDepth(uv, SAMPLE_TEXTURE2D_LOD(_FluidDepth, sampler_PointClamp, uv, 0).r);
            }

            // Uses whichever neighbor is closer in depth on each axis, so silhouettes do not
            // smear normals across the depth discontinuity.
            float3 ReconstructNormal(float2 uv, float3 center, float eyeDepth)
            {
                float2 texel = rcp(_ScaledScreenParams.xy);
                float dR = SAMPLE_TEXTURE2D_LOD(_FluidDepth, sampler_PointClamp, uv + float2(texel.x, 0), 0).r;
                float dL = SAMPLE_TEXTURE2D_LOD(_FluidDepth, sampler_PointClamp, uv - float2(texel.x, 0), 0).r;
                float dU = SAMPLE_TEXTURE2D_LOD(_FluidDepth, sampler_PointClamp, uv + float2(0, texel.y), 0).r;
                float dD = SAMPLE_TEXTURE2D_LOD(_FluidDepth, sampler_PointClamp, uv - float2(0, texel.y), 0).r;

                float3 ddx = abs(dR - eyeDepth) < abs(dL - eyeDepth)
                    ? WorldPositionFromEyeDepth(uv + float2(texel.x, 0), dR) - center
                    : center - WorldPositionFromEyeDepth(uv - float2(texel.x, 0), dL);
                float3 ddy = abs(dU - eyeDepth) < abs(dD - eyeDepth)
                    ? WorldPositionFromEyeDepth(uv + float2(0, texel.y), dU) - center
                    : center - WorldPositionFromEyeDepth(uv - float2(0, texel.y), dD);

                float3 n = normalize(cross(ddy, ddx));
                float3 toCamera = GetWorldSpaceViewDir(center);
                return dot(n, toCamera) < 0 ? -n : n;
            }

            float3 Triplanar(TEXTURE2D_PARAM(tex, samp), float3 p, float3 weights)
            {
                return SAMPLE_TEXTURE2D(tex, samp, p.zy).rgb * weights.x
                     + SAMPLE_TEXTURE2D(tex, samp, p.xz).rgb * weights.y
                     + SAMPLE_TEXTURE2D(tex, samp, p.xy).rgb * weights.z;
            }

            // Two scrolling ripple layers per projection plane, added as a slope perturbation.
            float3 ApplyRipples(float3 n, float3 positionWS, float strength)
            {
                float3 w = pow(abs(n), 4.0);
                w /= dot(w, 1.0);
                float t = _Time.y * _RippleSpeed;
                float3 perturb = 0;
                [unroll]
                for (int layer = 0; layer < 2; layer++)
                {
                    float scale = _RippleScale * (layer == 0 ? 1.0 : 2.37);
                    float2 scroll = layer == 0 ? float2(t, t * 0.6) : float2(-t * 0.7, t * 1.1);
                    float3 p = positionWS * scale;
                    float3 nx = UnpackNormal(SAMPLE_TEXTURE2D(_RippleNormal, sampler_LinearRepeat, p.zy + scroll));
                    float3 ny = UnpackNormal(SAMPLE_TEXTURE2D(_RippleNormal, sampler_LinearRepeat, p.xz + scroll));
                    float3 nz = UnpackNormal(SAMPLE_TEXTURE2D(_RippleNormal, sampler_LinearRepeat, p.xy + scroll));
                    perturb += w.x * float3(0, nx.y, nx.x) + w.y * float3(ny.x, 0, ny.y) + w.z * float3(nz.x, nz.y, 0);
                }
                return normalize(n + perturb * (0.5 * strength));
            }

            float4 Frag(FullscreenVaryings i) : SV_Target
            {
                float2 uv = GetNormalizedScreenSpaceUV(i.positionCS);
                float eyeDepth = SAMPLE_TEXTURE2D_LOD(_FluidDepth, sampler_PointClamp, uv, 0).r;
                if (eyeDepth >= NO_FLUID_DEPTH || eyeDepth > SceneEyeDepth(uv))
                    discard;

                float4 thicknessFoam = SAMPLE_TEXTURE2D_LOD(_FluidThickness, sampler_LinearClamp, uv, 0);
                float thickness = thicknessFoam.r;
                float surfaceAeration = thicknessFoam.g;
                float bubbles = 1.0 - exp(-thicknessFoam.b * _BubbleAmount);
                // Fast water (the falling stream, the plunge) has a broken, roughened surface:
                // stronger ripples and a silvery sheen, which is what makes a clear stream visible.
                float meanSpeed = thicknessFoam.a / max(thickness, 1e-4);
                float flow = saturate((meanSpeed - _FlowSpeedMin) / _FlowSpeedRange);

                float3 positionWS = WorldPositionFromEyeDepth(uv, eyeDepth);
                float3 geometricNormal = ReconstructNormal(uv, positionWS, eyeDepth);
                float3 n = ApplyRipples(geometricNormal, positionWS, _RippleStrength * (1.0 + 2.0 * flow));
                float3 v = normalize(GetWorldSpaceViewDir(positionWS));
                float nDotV = saturate(dot(n, v));

                // Refraction: bend the lookup into the opaque scene by the view-space normal,
                // and fall back to the straight lookup if the bent one lands on something in front.
                float3 nVS = TransformWorldToViewDir(n);
                float2 refractUV = uv - nVS.xy * _RefractionStrength * saturate(thickness * 4.0);
                if (SceneEyeDepth(refractUV) < eyeDepth)
                    refractUV = uv;
                float3 background = SAMPLE_TEXTURE2D_LOD(_SceneColor, sampler_LinearClamp, refractUV, 0).rgb;

                // Beer-Lambert absorption plus in-scattered ambient light along the same path.
                float3 transmittance = exp(-_AbsorptionCoefficients.rgb * thickness);
                float3 ambient = SampleSH(n);
                float3 transmitted = background * transmittance + _ScatterColor.rgb * ambient * (1.0 - transmittance);
                // Bubbles scatter light white and hide what is behind them.
                transmitted = lerp(transmitted, _FoamTint.rgb * ambient * 0.9, bubbles);
                transmitted = lerp(transmitted, _FoamTint.rgb * ambient, flow * _FlowSheen);

                float3 r = reflect(-v, n);
                float mip = PerceptualRoughnessToMipmapLevel(1.0 - _Smoothness);
                float4 encodedEnv = SAMPLE_TEXTURECUBE_LOD(_GlossyEnvironmentCubeMap, sampler_GlossyEnvironmentCubeMap, r, mip);
                float3 reflection = DecodeHDREnvironment(encodedEnv, _GlossyEnvironmentCubeMap_HDR);

                float fresnel = 0.02 + 0.98 * pow(1.0 - nDotV, 5.0);
                float3 color = lerp(transmitted, reflection, fresnel);

                Light mainLight = GetMainLight();
                float3 h = normalize(mainLight.direction + v);
                float roughness = max(1.0 - _Smoothness, 0.02);
                float a2 = roughness * roughness * roughness * roughness;
                float nDotH = saturate(dot(n, h));
                float denom = nDotH * nDotH * (a2 - 1.0) + 1.0;
                float ggx = a2 / (PI * denom * denom);
                color += mainLight.color * ggx * fresnel * saturate(dot(n, mainLight.direction));

                // Foam: aeration thresholds a photographed sea-foam lace, so light aeration shows
                // as sparse streaks and heavy aeration as solid whitewater.
                float3 w = pow(abs(geometricNormal), 4.0);
                w /= dot(w, 1.0);
                float3 foamP = positionWS * _FoamScale + float3(0, _Time.y * 0.03, 0);
                float lace = Triplanar(TEXTURE2D_ARGS(_FoamMask, sampler_LinearRepeat), foamP, w).r;
                float3 foamAlbedo = Triplanar(TEXTURE2D_ARGS(_FoamColor, sampler_LinearRepeat), foamP, w) * _FoamTint.rgb;
                float foamCoverage = saturate(surfaceAeration * _FoamAmount);
                float foam = saturate((lace - (1.0 - foamCoverage)) * 3.0 + foamCoverage * foamCoverage);
                float3 foamLit = foamAlbedo * (ambient + mainLight.color * saturate(dot(geometricNormal, mainLight.direction)));
                color = lerp(color, foamLit, foam);

                float alpha = saturate(thickness * _EdgeOpacity + foam + bubbles);
                return float4(color, alpha);
            }
            ENDHLSL
        }
    }
}
