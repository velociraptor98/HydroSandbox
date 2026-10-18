#ifndef HYDRO_OCEAN_COMMON_INCLUDED
#define HYDRO_OCEAN_COMMON_INCLUDED

// The FFT ocean's textures (OceanWaves) and the particle-water zone cut out of it
// (FluidOceanBoundary), shared by the ocean surface shader and the surf-zone forcing kernel.
// Standalone: compute shaders cannot include the render pipeline's libraries.

Texture2D<float4> _OceanDisplacement0;
Texture2D<float4> _OceanDisplacement1;
SamplerState sampler_ocean_trilinear_repeat;
float4 _OceanCascadeSizes;          // xy patch sizes, zw their inverses

// Particle zone: xy its min corner in xz, zw its max corner.
float4 _OceanZone;
// x width of the band at the zone's edge where the two waters overlap, y how far the
// heightfield sinks across it, z 1 when a zone exists, w the width of the band's outer part
// where the particle water fades in and the heightfield stays at full height.
float4 _OceanZoneParams;
// Waves ease down toward the zone, as the swell does over the shoals around an island: x the
// amplitude scale at the zone's edge and inside it, y the distance outside the edge over
// which it recovers to full height.
float4 _OceanZoneShelter;

float3 OceanDisplacement(float2 xz, float lod0, float lod1)
{
    return _OceanDisplacement0.SampleLevel(sampler_ocean_trilinear_repeat, xz * _OceanCascadeSizes.z, lod0).xyz
         + _OceanDisplacement1.SampleLevel(sampler_ocean_trilinear_repeat, xz * _OceanCascadeSizes.w, lod1).xyz;
}

// The displacement maps are Lagrangian: they move a surface point from its rest position. The
// rest position of whatever point lies over xz now is found by fixed-point iteration.
float2 OceanRestPosition(float2 xz, float amplitude)
{
    float2 rest = xz;
    [unroll]
    for (int i = 0; i < 3; i++)
        rest = xz - OceanDisplacement(rest, 0, 0).xz * amplitude;
    return rest;
}

// Distance inside the zone's edge; negative outside it.
float OceanZoneDistance(float2 xz)
{
    float2 fromMin = xz - _OceanZone.xy;
    float2 fromMax = _OceanZone.zw - xz;
    return min(min(fromMin.x, fromMin.y), min(fromMax.x, fromMax.y));
}

float OceanShelter(float2 xz)
{
    if (_OceanZoneParams.z <= 0.0)
        return 1.0;
    // Outside the box the distance to it is Euclidean, so the sheltered area rounds its corners.
    float2 outside = max(max(_OceanZone.xy - xz, xz - _OceanZone.zw), 0.0);
    return lerp(_OceanZoneShelter.x, 1.0, smoothstep(0.0, _OceanZoneShelter.y, length(outside)));
}

// A tsunami (OceanTsunami): one solitary wave crossing the sea toward a target point.
// 0: travel direction xz, crest position along it relative to the target, amplitude (0 = none).
// 1: width ahead of the crest and behind it (the front is steeper), lateral half-width, speed.
// 2: target xz, depth used for the wave's flow speed.
float4 _Tsunami0;
float4 _Tsunami1;
float4 _Tsunami2;

// Height above the sea at xz, and its gradient: a sech^2 profile along the travel direction,
// with a Gaussian falloff across it.
float TsunamiHeight(float2 xz, out float2 gradient)
{
    float amplitude = _Tsunami0.w;
    float2 dir = _Tsunami0.xy;
    float2 side = float2(-dir.y, dir.x);
    float2 rel = xz - _Tsunami2.xy;
    float along = dot(rel, dir) - _Tsunami0.z;
    float across = dot(rel, side);
    // Guarded: the widths are 0 until OceanTsunami first publishes them.
    float width = max(along > 0.0 ? _Tsunami1.x : _Tsunami1.y, 1e-3);
    float lateralSq = max(_Tsunami1.z * _Tsunami1.z, 1e-3);
    float t = tanh(clamp(along / width, -15.0, 15.0));
    float lateral = exp(-across * across / lateralSq);
    // amplitude is 0 between waves, which zeroes both.
    float h = max(amplitude, 0.0) * (1.0 - t * t) * lateral;
    gradient = dir * (-2.0 * h * t / width) + side * (-2.0 * h * across / lateralSq);
    return h;
}

// Depth-averaged flow under a long wave of height h: it carries c h / (depth + h) forward.
float3 TsunamiFlow(float h)
{
    float speed = _Tsunami1.w * h / (_Tsunami2.z + h);
    return float3(_Tsunami0.x, 0.0, _Tsunami0.y) * speed;
}

#endif
