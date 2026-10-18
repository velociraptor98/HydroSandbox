#ifndef HYDRO_OCEAN_FOAM_INCLUDED
#define HYDRO_OCEAN_FOAM_INCLUDED

// Whitecap foam of the FFT ocean (OceanWaves), shared by the ocean surface and by particle
// water inside an ocean zone, so the foam pattern carries on across the zone's edge.
// Needs OceanCommon.hlsl and the render pipeline's Core.hlsl.

TEXTURE2D(_OceanFoam0);
TEXTURE2D(_OceanFoam1);
// The ocean surface's foam lace, published by OceanSurface for the particle water to use.
TEXTURE2D(_OceanFoamPattern);
float _OceanFoamPatternScale;       // lace repeats per meter
float4 _OceanFoamColor;

// Visible foam at a rest position: whitecaps and the streaks they leave, broken up by the
// photographed lace.
float OceanFoamCoverage(float2 restXZ, TEXTURE2D_PARAM(pattern, patternSampler), float patternScale)
{
    float foam = SAMPLE_TEXTURE2D(_OceanFoam0, sampler_ocean_trilinear_repeat, restXZ * _OceanCascadeSizes.z).x
               + 0.6 * SAMPLE_TEXTURE2D(_OceanFoam1, sampler_ocean_trilinear_repeat, restXZ * _OceanCascadeSizes.w).x;
    float lace = SAMPLE_TEXTURE2D(pattern, patternSampler, restXZ * patternScale).r;
    float lace2 = SAMPLE_TEXTURE2D(pattern, patternSampler, restXZ * patternScale * 3.1 + 0.37).r;
    float coverage = saturate(foam * 1.3);
    return saturate(saturate((lace * 0.6 + lace2 * 0.4) - (1.0 - coverage)) * 2.0 + coverage * coverage * 0.35);
}

float3 OceanFoamLit(float3 foamColor, float3 ambient, float3 n, Light light)
{
    return foamColor * (ambient * 1.4 + light.color * (0.4 + 0.6 * saturate(dot(n, light.direction))));
}

#endif
