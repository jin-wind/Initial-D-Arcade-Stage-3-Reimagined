#pragma once
#include "original_dynamics.h"

namespace idas3::original {
inline OriginalTailState canonicalRollbackStatistics(OriginalTailState tail){
    // Before statistics have samples, original 15ECE0 publishes 0/0 for
    // accelerator/brake fractions. IEEE-754 leaves the NaN sign/payload to the
    // platform; Android and Apple produce different bits for the same state.
    // These two output-only float lanes are not solver inputs. Normalize only
    // their NaNs in the checksum copy, keeping all finite values (including
    // signed zero), infinities and integer statistic lanes bit-exact.
    for(unsigned index:{1u,2u}){
        auto& bits=tail.statistics0C91FB0C[index];
        if((bits&0x7f800000u)==0x7f800000u&&(bits&0x007fffffu)!=0)bits=0x7fc00000u;
    }
    return tail;
}
}
