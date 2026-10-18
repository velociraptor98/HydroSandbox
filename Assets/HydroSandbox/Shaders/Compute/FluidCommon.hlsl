#ifndef HYDRO_FLUID_COMMON_INCLUDED
#define HYDRO_FLUID_COMMON_INCLUDED

#define THREAD_GROUP_SIZE 256
#define MAX_NEIGHBORS 64

uint _ParticleCount;
float _DeltaTime;
float3 _Gravity;
float _MaxSpeed;

float _SmoothingRadius;
float _SmoothingRadiusSq;
float _InvCellSize;
uint _GridBitsX;
uint _GridBitsY;
uint _GridBitsZ;

float _ParticleMass;
float _InvRestDensity;
float _Poly6Coeff;
float _SpikyGradCoeff;
float _ConstraintRelaxation;
float _Cohesion;
float _SCorrK;
float _SCorrInvWdq;
float _Viscosity;

float3 _ContainerMin;
float3 _ContainerMax;

// Multi-liquid mixing (FluidMixing). Each particle's composition (float4) holds the fraction of
// each liquid; per-liquid properties are float4s blended with dot(composition, property), and
// pair properties are symmetric matrices evaluated as dot(ci, mul(M, cj)).
uint _MultiFluid;
float4 _LiquidDensity;
float4 _LiquidViscosity;
float4 _LiquidWallFriction;
float _BuoyancyScale;
float4x4 _PairDiffusion;
float4x4 _PairTension;

// _ParticleCount is the pool capacity. _FluidState[0] is the live count: after every sort the
// live particles occupy [0, count) and the dead are packed behind them. Liveness itself is
// position.w (1 alive, 0 dead), which every kernel writing a position must preserve.
StructuredBuffer<uint> _FluidState;

#define DEAD_CELL 0xffffffffu

bool IsAlive(float4 p)
{
    return p.w > 0.5;
}

float Poly6(float r2)
{
    float d = _SmoothingRadiusSq - r2;
    return _Poly6Coeff * d * d * d;
}

uint WangHash(uint s)
{
    s = (s ^ 61u) ^ (s >> 16);
    s *= 9u;
    s ^= s >> 4;
    s *= 0x27d4eb2du;
    s ^= s >> 15;
    return s;
}

// Unit direction fixed per particle pair and antisymmetric in (i, j).
float3 PairDirection(uint i, uint j)
{
    uint h1 = WangHash(min(i, j) * 9781u + max(i, j) * 6271u);
    uint h2 = WangHash(h1);
    float z = (h1 & 0xffffu) * (2.0 / 65535.0) - 1.0;
    float phi = (h2 & 0xffffu) * (6.2831853 / 65535.0);
    float s = sqrt(saturate(1.0 - z * z));
    float3 d = float3(s * cos(phi), s * sin(phi), z);
    return i < j ? d : -d;
}

// Gradient with respect to particle i, r = p_i - p_j. Exactly coincident particles (box
// clamping stacks them in corners) have no gradient direction and would never separate, so
// they get a pseudo-random one at the kernel's full r -> 0 magnitude.
float3 SpikyGradient(float3 r, float r2, uint i, uint j)
{
    float len = sqrt(r2);
    float d = _SmoothingRadius - len;
    float3 dir = len > 1e-6 ? r / len : PairDirection(i, j);
    return (_SpikyGradCoeff * d * d) * dir;
}

int3 CellCoord(float3 p)
{
    return (int3)floor(p * _InvCellSize);
}

// 10 bits per axis: cell ids are exact within a 1024^3 window and wrap outside it.
// The wrap only adds false candidates, which the distance test rejects.
uint PackCell(int3 cell)
{
    uint3 u = (uint3)cell & 1023;
    return u.x | (u.y << 10) | (u.z << 20);
}

// Modular spatial hash: cell coordinates wrapped into a power-of-two grid, x in the lowest bits.
// Unlike a scrambling hash this keeps neighboring cells close in sorted particle order (the
// solver's neighbor loops are bound by those reads) and makes each x-row of three cells one
// contiguous key range. Cells a full grid period apart collide; the cell-id test filters them.
uint CellKey(uint cellId)
{
    uint x = cellId & ((1u << _GridBitsX) - 1u);
    uint y = (cellId >> 10) & ((1u << _GridBitsY) - 1u);
    uint z = (cellId >> 20) & ((1u << _GridBitsZ) - 1u);
    return x | (y << _GridBitsX) | (z << (_GridBitsX + _GridBitsY));
}

#endif
