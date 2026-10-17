#ifndef HYDRO_FLUID_GRID_INCLUDED
#define HYDRO_FLUID_GRID_INCLUDED

#include "FluidCommon.hlsl"

StructuredBuffer<uint> _CellStart;
StructuredBuffer<uint> _CellCount;
StructuredBuffer<uint> _SortedCellId;

// Visits every particle j in the 27 cells around `position`, one x-row of three cells at a
// time. A row is a single contiguous key range unless it straddles the grid's x wrap, in which
// case its cells are walked separately. The cell-id test rejects particles from cells that
// share a key a grid period away.
#define FOR_EACH_GRID_NEIGHBOR(position, j)                                                       \
    int3 nbBase_ = CellCoord(position);                                                      \
    for (int nbZ_ = -1; nbZ_ <= 1; nbZ_++)                                                   \
    for (int nbY_ = -1; nbY_ <= 1; nbY_++)                                                   \
    {                                                                                        \
        int3 nbRow_ = nbBase_ + int3(0, nbY_, nbZ_);                                         \
        uint nbIdL_ = PackCell(nbRow_ - int3(1, 0, 0));                                      \
        uint nbIdC_ = PackCell(nbRow_);                                                      \
        uint nbIdR_ = PackCell(nbRow_ + int3(1, 0, 0));                                      \
        uint nbKeyL_ = CellKey(nbIdL_);                                                      \
        uint nbKeyR_ = CellKey(nbIdR_);                                                      \
        bool nbContiguous_ = nbKeyR_ == nbKeyL_ + 2;                                         \
        uint nbRanges_ = nbContiguous_ ? 1 : 3;                                              \
        for (uint nbRange_ = 0; nbRange_ < nbRanges_; nbRange_++)                            \
        {                                                                                    \
            uint nbFirst_ = nbContiguous_ ? nbKeyL_                                          \
                : CellKey(PackCell(nbRow_ + int3((int)nbRange_ - 1, 0, 0)));                 \
            uint nbLast_ = nbContiguous_ ? nbKeyR_ : nbFirst_;                               \
            uint nbEnd_ = _CellStart[nbLast_] + _CellCount[nbLast_];                         \
            for (uint j = _CellStart[nbFirst_]; j < nbEnd_; j++)                             \
            {                                                                                \
                uint nbId_ = _SortedCellId[j];                                               \
                if (nbId_ != nbIdL_ && nbId_ != nbIdC_ && nbId_ != nbIdR_)                   \
                    continue;

#define END_FOR_EACH_GRID_NEIGHBOR }}}

#endif
