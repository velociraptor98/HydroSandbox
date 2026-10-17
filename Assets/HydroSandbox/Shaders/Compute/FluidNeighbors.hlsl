#ifndef HYDRO_FLUID_NEIGHBORS_INCLUDED
#define HYDRO_FLUID_NEIGHBORS_INCLUDED

#include "FluidCommon.hlsl"

// Neighbor-major (entry k of particle i at k * _ParticleCount + i) so a SIMD group reading the
// same k for consecutive particles touches consecutive addresses. Self is not listed.
StructuredBuffer<uint> _NeighborList;
StructuredBuffer<uint> _NeighborCount;

#define FOR_EACH_NEIGHBOR(i, j)                                                     \
    uint nbCount_ = _NeighborCount[i];                                              \
    for (uint nbK_ = 0; nbK_ < nbCount_; nbK_++)                                    \
    {                                                                               \
        uint j = _NeighborList[nbK_ * _ParticleCount + i];

#define END_FOR_EACH_NEIGHBOR }

#endif
