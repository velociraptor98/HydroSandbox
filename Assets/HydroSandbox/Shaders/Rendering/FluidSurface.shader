// Screen-space fluid rendering (van der Laan et al. 2009 style), driven by FluidSurfaceFeature:
//   0 ParticleDepth      sphere impostors -> nearest linear eye depth
//   1 ParticleThickness  additive chord length (R) and speed-weighted chord (G)
//   2 BlurDepth          separable bilateral filter on eye depth, run once per axis
//   3 Composite          normals from depth, refraction, Fresnel reflection, absorption
//   4 WhitewaterSplat    soft splats of spray/foam/bubble particles: visible (R), submerged (G)
//   5 WhitewaterComposite  lights the accumulated whitewater and blends it over the water
//   6 ParticleOptics     multi-liquid only: additive absorption and scattering optical depth
//                        (chord x each particle's composition-blended coefficients), RGB each.
//                        Splats are _OpticsRadiusScale times wider and 1/scale^3 as dense, which
//                        keeps the optical depth but smooths the particle grain out of the color.
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
        // Last substep's neighbor counts. Particles with at most _DropletMaxNeighbors are loose
        // droplets: they are left out of the surface (where they would be solid glass beads) and
        // drawn as spray by the whitewater pass instead.
        StructuredBuffer<uint> _NeighborCount;
        uint _DropletMaxNeighbors;
        float _ParticleRadius;
        float _DepthRadiusScale;
        float _ThicknessScale;

        TEXTURE2D(_SceneDepth);
        TEXTURE2D(_SceneColor);
        TEXTURE2D(_FluidDepth);
        TEXTURE2D(_FluidThickness);
        TEXTURE2D(_FluidAbsorption);
        TEXTURE2D(_FluidScattering);
        TEXTURE2D(_BlurSource);

        // Multi-liquid (FluidMixing): per-particle liquid fractions and per-liquid optics.
        uint _MultiFluid;
        float _OpticsRadiusScale;
        StructuredBuffer<float4> _Composition;
        float4 _LiquidAbsorption[4];
        float4 _LiquidScattering[4];

        float SceneEyeDepth(float2 uv)
        {
            return LinearEyeDepth(SAMPLE_TEXTURE2D_LOD(_SceneDepth, sampler_PointClamp, uv, 0).r, _ZBufferParams);
        }

        float3 WorldPositionFromEyeDepth(float2 uv, float eyeDepth)
        {
            float deviceDepth = (rcp(eyeDepth) - _ZBufferParams.w) / _ZBufferParams.z;
            return ComputeWorldSpacePosition(uv, deviceDepth, UNITY_MATRIX_I_VP);
        }

        // Scene ambient probe evaluated on the CPU: SampleSH reads per-object probe data, which a
        // full-screen procedural draw does not have.
        float4 _AmbientUp;
        float4 _AmbientHorizon;
        float4 _AmbientDown;

        float3 SampleAmbient(float3 n)
        {
            return n.y >= 0.0 ? lerp(_AmbientHorizon.rgb, _AmbientUp.rgb, n.y)
                              : lerp(_AmbientHorizon.rgb, _AmbientDown.rgb, -n.y);
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
            float speed : TEXCOORD3;
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
            o.speed = length(_Velocities[instanceId].xyz);
            // Dead pool slots (w = 0) and loose droplets collapse to a degenerate quad outside the
            // clip volume.
            if (particle.w < 0.5 || _NeighborCount[instanceId] <= _DropletMaxNeighbors)
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

            float2 Frag(ParticleVaryings i) : SV_Target
            {
                float r2 = dot(i.corner, i.corner);
                clip(1.0 - r2);
                float z = sqrt(1.0 - r2);
                // Particles behind scene geometry must not add thickness to what is in front.
                float frontEyeDepth = -(i.centerVS.z + z * i.radius);
                float2 uv = GetNormalizedScreenSpaceUV(i.positionCS);
                clip(SceneEyeDepth(uv) - frontEyeDepth);
                float chord = 2.0 * z * i.radius * _ThicknessScale;
                return float2(chord, chord * i.speed);
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
            int _BlurMaxRadius;         // taps per side
            float _BlurSpread;          // 1: spread taps over the full radius (iterations after the first)

            // Off-screen reads count as no fluid: clamped, they would repeat the border pixel and
            // smear it into stripes.
            float BlurTap(float2 uv)
            {
                if (any(uv < 0.0) || any(uv > 1.0))
                    return NO_FLUID_DEPTH;
                return SAMPLE_TEXTURE2D_LOD(_BlurSource, sampler_PointClamp, uv, 0).r;
            }

            float Frag(FullscreenVaryings i) : SV_Target
            {
                float2 uv = GetNormalizedScreenSpaceUV(i.positionCS);
                float center = SAMPLE_TEXTURE2D_LOD(_BlurSource, sampler_PointClamp, uv, 0).r;
                if (center >= NO_FLUID_DEPTH)
                    return center;

                // Pixels covered by the world-space radius at this depth. abs(): the projection's
                // y scale is negative when URP flips rendering into a texture.
                float pixelRadius = _BlurWorldRadius * abs(UNITY_MATRIX_P._m11) * 0.5 * _ScaledScreenParams.y / center;
                // Close up the radius outgrows the tap budget. Clamping it would shrink the filter
                // below one particle and leave every sphere cap showing as a bump, so later
                // iterations spread their taps instead: the filter keeps its world size at any zoom
                // for the same cost. Spread taps skip pixels, so they are only valid on depth the
                // first (dense) iteration has already smoothed: on raw depth the creases between
                // sphere caps alias into streaks, as does what detail remains once taps are more
                // than about 4 pixels apart. Beyond that, extreme close-ups get a smaller filter.
                int radius = clamp((int)pixelRadius, 1, _BlurMaxRadius);
                float2 tapStep = _BlurDirection * (_BlurSpread > 0.5 ? clamp(pixelRadius / radius, 1.0, 4.0) : 1.0);
                float sigma = max(radius * 0.5, 1.0);
                float invTwoSigma2 = 0.5 / (sigma * sigma);
                float invFalloff2 = rcp(_BlurDepthFalloff * _BlurDepthFalloff);

                // Range weights compare taps against the local surface plane, not the center depth:
                // at grazing angles a flat surface changes depth quickly per pixel, and a plain
                // depth test would refuse to smooth it, leaving the particle rows as terraces. The
                // slope is the gentler one-sided difference, so silhouettes still stop the blur. It
                // is measured over half the radius so it follows the surface, not one particle's cap.
                int stride = max(radius >> 1, 1);
                float dPrev = BlurTap(uv - tapStep * stride);
                float dNext = BlurTap(uv + tapStep * stride);
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
                    float d = BlurTap(uv + tapStep * k);
                    // Spread taps cross the water's edge one every few pixels; dropping each one
                    // as it leaves the water turns the edge band into a staircase that the normals
                    // show as stripes. Off the edge they count as the local surface plane instead.
                    if (d >= NO_FLUID_DEPTH)
                    {
                        if (_BlurSpread < 0.5)
                            continue;
                        d = center + slope * k;
                    }
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

            float4 _AbsorptionCoefficients; // per meter, rgb
            float4 _ScatterColor;
            float _RefractionStrength;
            float _RippleStrength;
            float _RippleScale;
            float _RippleSpeed;
            float _FlowSpeedMin;
            float _FlowSpeedRange;
            float _FlowSheen;
            float _EdgeOpacity;
            float _Smoothness;

            float3 FluidWorldPosition(float2 uv)
            {
                return WorldPositionFromEyeDepth(uv, SAMPLE_TEXTURE2D_LOD(_FluidDepth, sampler_PointClamp, uv, 0).r);
            }

            // Central differences, except across a depth discontinuity (a silhouette), where the
            // neighbor closer in depth is used so normals do not smear across it. Choosing the
            // closer neighbor everywhere flips between forward and backward differences from
            // pixel to pixel on smooth water, which shows as a crosshatch.
            // Differences span two pixels: refraction turns sub-pixel normal noise into visible
            // shimmer on a detailed bed, and a wider stencil averages it out.
            float3 ReconstructNormal(float2 uv, float3 center, float eyeDepth)
            {
                float2 texel = 2.0 * rcp(_ScaledScreenParams.xy);
                float dR = SAMPLE_TEXTURE2D_LOD(_FluidDepth, sampler_PointClamp, uv + float2(texel.x, 0), 0).r;
                float dL = SAMPLE_TEXTURE2D_LOD(_FluidDepth, sampler_PointClamp, uv - float2(texel.x, 0), 0).r;
                float dU = SAMPLE_TEXTURE2D_LOD(_FluidDepth, sampler_PointClamp, uv + float2(0, texel.y), 0).r;
                float dD = SAMPLE_TEXTURE2D_LOD(_FluidDepth, sampler_PointClamp, uv - float2(0, texel.y), 0).r;

                float3 right = WorldPositionFromEyeDepth(uv + float2(texel.x, 0), dR);
                float3 left = WorldPositionFromEyeDepth(uv - float2(texel.x, 0), dL);
                float3 up = WorldPositionFromEyeDepth(uv + float2(0, texel.y), dU);
                float3 down = WorldPositionFromEyeDepth(uv - float2(0, texel.y), dD);
                float jump = 2.0 * _ParticleRadius;
                float3 ddx = max(abs(dR - eyeDepth), abs(dL - eyeDepth)) < jump ? 0.5 * (right - left)
                    : abs(dR - eyeDepth) < abs(dL - eyeDepth) ? right - center : center - left;
                float3 ddy = max(abs(dU - eyeDepth), abs(dD - eyeDepth)) < jump ? 0.5 * (up - down)
                    : abs(dU - eyeDepth) < abs(dD - eyeDepth) ? up - center : center - down;

                float3 n = normalize(cross(ddy, ddx));
                float3 toCamera = GetWorldSpaceViewDir(center);
                return dot(n, toCamera) < 0 ? -n : n;
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

            // Beer-Lambert absorption plus in-scattered light along the same path. One liquid uses
            // the surface's uniform coefficients; mixed liquids use the accumulated optical depths,
            // whose scattering share sets the color of the in-scattered light.
            float3 Transmit(float2 uv, float3 background, float thickness, float3 ambient)
            {
                if (_MultiFluid == 0)
                {
                    float3 transmittance = exp(-_AbsorptionCoefficients.rgb * thickness);
                    return background * transmittance + _ScatterColor.rgb * ambient * (1.0 - transmittance);
                }
                float3 absorbed = SAMPLE_TEXTURE2D_LOD(_FluidAbsorption, sampler_LinearClamp, uv, 0).rgb;
                float3 scattered = SAMPLE_TEXTURE2D_LOD(_FluidScattering, sampler_LinearClamp, uv, 0).rgb;
                float3 extinction = absorbed + scattered;
                float3 transmittance = exp(-extinction);
                float3 albedo = scattered / max(extinction, 1e-4);
                float3 light = ambient + GetMainLight().color * 0.35;
                return background * transmittance + albedo * light * (1.0 - transmittance);
            }

            float4 Frag(FullscreenVaryings i) : SV_Target
            {
                float2 uv = GetNormalizedScreenSpaceUV(i.positionCS);
                float eyeDepth = SAMPLE_TEXTURE2D_LOD(_FluidDepth, sampler_PointClamp, uv, 0).r;
                // Thin water lying on the bed is nearly coplanar with it; a strict depth test flips
                // pixel to pixel there into a moire, so water up to a particle radius behind the
                // scene still shows.
                if (eyeDepth >= NO_FLUID_DEPTH || eyeDepth > SceneEyeDepth(uv) + _ParticleRadius)
                    discard;

                float2 thicknessSpeed = SAMPLE_TEXTURE2D_LOD(_FluidThickness, sampler_LinearClamp, uv, 0).rg;
                float thickness = thicknessSpeed.r;
                // Fast water (the falling stream, the plunge) has a broken, roughened surface:
                // stronger ripples and a silvery sheen, which is what makes a clear stream visible.
                float meanSpeed = thicknessSpeed.g / max(thickness, 1e-4);
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

                float3 ambient = SampleAmbient(n);
                float3 transmitted = Transmit(uv, background, thickness, ambient);
                transmitted = lerp(transmitted, ambient, flow * _FlowSheen);

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

                float alpha = saturate(thickness * _EdgeOpacity);
                return float4(color, alpha);
            }
            ENDHLSL
        }

        Pass
        {
            Name "WhitewaterSplat"
            ZTest Always
            Blend One One

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            #define TYPE_FOAM 1
            #define TYPE_BUBBLE 2

            StructuredBuffer<float4> _WhitewaterPositions;
            StructuredBuffer<float4> _WhitewaterVelocities;
            // 0: whitewater particles. 1: loose fluid droplets from _Positions, drawn as spray.
            uint _SplatSource;
            float _DropletRadius;
            TEXTURE2D(_FoamMask);
            float _WhitewaterRadius;
            float _WhitewaterMinPixels;
            float _SprayIntensity;
            float _FoamIntensity;
            float _BubbleIntensity;
            float _BubbleAttenuation;
            float _FoamScale;

            struct SplatVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 corner : TEXCOORD0;
                float eyeDepth : TEXCOORD1;
                float intensity : TEXCOORD2;
                float isFoam : TEXCOORD3;
                float3 positionWS : TEXCOORD4;
            };

            SplatVaryings Vert(uint vertexId : SV_VertexID, uint instanceId : SV_InstanceID)
            {
                SplatVaryings o;
                float4 particle;
                uint type;
                float radius;
                bool visible;
                if (_SplatSource == 0)
                {
                    particle = _WhitewaterPositions[instanceId];
                    type = (uint)_WhitewaterVelocities[instanceId].w;
                    radius = _WhitewaterRadius;
                    visible = particle.w > 0.0;
                }
                else
                {
                    float4 fluid = _Positions[instanceId];
                    particle = float4(fluid.xyz, 1.0);
                    type = 0;
                    radius = _DropletRadius;
                    visible = fluid.w > 0.5 && _NeighborCount[instanceId] <= _DropletMaxNeighbors;
                }
                float3 centerVS = TransformWorldToView(particle.xyz);
                float eyeDepth = -centerVS.z;

                // Keep splats at least _WhitewaterMinPixels wide and dim them by the area they
                // grew by, so distant whitewater thins out instead of aliasing to nothing.
                float pixelsPerMeter = abs(UNITY_MATRIX_P._m11) * 0.5 * _ScaledScreenParams.y / max(eyeDepth, 1e-3);
                float pixelRadius = radius * pixelsPerMeter;
                float grow = max(_WhitewaterMinPixels / max(pixelRadius, 1e-4), 1.0);
                radius *= grow;

                float intensity = type == TYPE_BUBBLE ? _BubbleIntensity : (type == TYPE_FOAM ? _FoamIntensity : _SprayIntensity);
                intensity *= saturate(particle.w / 0.5) / (grow * grow);

                float2 corner = kCorners[vertexId];
                o.positionCS = TransformWViewToHClip(centerVS + float3(corner * radius, 0));
                o.corner = corner;
                o.eyeDepth = eyeDepth;
                o.intensity = intensity;
                o.isFoam = type == TYPE_FOAM ? 1.0 : 0.0;
                o.positionWS = particle.xyz;
                if (!visible || eyeDepth <= 0.0)
                    o.positionCS = float4(2, 2, 2, 1);
                return o;
            }

            float2 Frag(SplatVaryings i) : SV_Target
            {
                float r2 = dot(i.corner, i.corner);
                clip(1.0 - r2);
                float2 uv = GetNormalizedScreenSpaceUV(i.positionCS);
                clip(SceneEyeDepth(uv) - i.eyeDepth);

                float value = i.intensity * exp(-4.0 * r2);
                // Surface foam gathers into lace: the photographed foam pattern, fixed in world
                // space, breaks clusters of foam particles into strands and holes.
                if (i.isFoam > 0.5)
                {
                    float lace = SAMPLE_TEXTURE2D_LOD(_FoamMask, sampler_LinearRepeat, i.positionWS.xz * _FoamScale, 0).r;
                    value *= lerp(0.25, 1.6, lace);
                }

                // Splats more than a particle radius behind the water's front surface are seen
                // through it (submerged bubbles), attenuated by the water in front.
                float frontDepth = SAMPLE_TEXTURE2D_LOD(_FluidDepth, sampler_PointClamp, uv, 0).r;
                float submerged = i.eyeDepth - frontDepth;
                if (frontDepth < NO_FLUID_DEPTH && submerged > _WhitewaterRadius * 3.0)
                    return float2(0, value * exp(-submerged * _BubbleAttenuation));
                return float2(value, 0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "WhitewaterComposite"
            ZTest Always
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex FullscreenVert
            #pragma fragment Frag

            TEXTURE2D(_Whitewater);
            float4 _FoamTint;
            float4 _ScatterColor;

            float4 Frag(FullscreenVaryings i) : SV_Target
            {
                float2 uv = GetNormalizedScreenSpaceUV(i.positionCS);
                float2 density = SAMPLE_TEXTURE2D_LOD(_Whitewater, sampler_PointClamp, uv, 0).rg;
                float visible = 1.0 - exp(-density.r);
                float submerged = (1.0 - exp(-density.g)) * 0.8;
                float alpha = 1.0 - (1.0 - visible) * (1.0 - submerged);
                if (alpha < 1e-3)
                    discard;

                // Whitewater scatters light many times, so it stays bright in shade: boosted sky
                // ambient plus a wrapped share of the sun rather than a hard N.L.
                Light mainLight = GetMainLight();
                float3 lit = _FoamTint.rgb * (SampleAmbient(float3(0, 1, 0)) * 1.6 + mainLight.color * 0.7);
                float3 bubbleLit = lit * lerp(float3(1, 1, 1), _ScatterColor.rgb * 3.0, 0.4);
                float3 color = (lit * visible + bubbleLit * submerged * (1.0 - visible)) / alpha;
                return float4(color, alpha);
            }
            ENDHLSL
        }

        Pass
        {
            Name "ParticleOptics"
            ZTest Always
            Blend One One

            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag

            struct OpticsVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 corner : TEXCOORD0;
                float3 centerVS : TEXCOORD1;
                float radius : TEXCOORD2;
                float3 absorption : TEXCOORD3;
                float3 scattering : TEXCOORD4;
            };

            struct OpticsTargets
            {
                float4 absorption : SV_Target0;
                float4 scattering : SV_Target1;
            };

            OpticsVaryings Vert(uint vertexId : SV_VertexID, uint instanceId : SV_InstanceID)
            {
                ParticleVaryings p = ParticleVert(vertexId, instanceId, _OpticsRadiusScale);
                OpticsVaryings o;
                o.positionCS = p.positionCS;
                o.corner = p.corner;
                o.centerVS = p.centerVS;
                o.radius = p.radius;
                float4 c = _Composition[instanceId];
                o.absorption = c.x * _LiquidAbsorption[0].rgb + c.y * _LiquidAbsorption[1].rgb
                             + c.z * _LiquidAbsorption[2].rgb + c.w * _LiquidAbsorption[3].rgb;
                o.scattering = c.x * _LiquidScattering[0].rgb + c.y * _LiquidScattering[1].rgb
                             + c.z * _LiquidScattering[2].rgb + c.w * _LiquidScattering[3].rgb;
                return o;
            }

            OpticsTargets Frag(OpticsVaryings i)
            {
                float r2 = dot(i.corner, i.corner);
                clip(1.0 - r2);
                float z = sqrt(1.0 - r2);
                float frontEyeDepth = -(i.centerVS.z + z * i.radius);
                float2 uv = GetNormalizedScreenSpaceUV(i.positionCS);
                clip(SceneEyeDepth(uv) - frontEyeDepth);
                float chord = 2.0 * z * i.radius * _ThicknessScale / (_OpticsRadiusScale * _OpticsRadiusScale * _OpticsRadiusScale);
                OpticsTargets o;
                o.absorption = float4(chord * i.absorption, 0);
                o.scattering = float4(chord * i.scattering, 0);
                return o;
            }
            ENDHLSL
        }
    }
}
