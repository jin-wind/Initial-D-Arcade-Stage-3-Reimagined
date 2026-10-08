#include "original_rollback_digest.h"
#include <bit>
#include <cstdio>
#include <stdexcept>
using namespace idas3::original;
int main(){try{
    auto check=[](bool ok,const char* message){if(!ok)throw std::runtime_error(message);};
    OriginalTailState source;source.statistics0C91FB0C[1]=0xffc00000u;
    source.statistics0C91FB0C[2]=0x7fa01234u;source.statistics0C91FB0C[4]=0xffffffffu;
    auto canonical=canonicalRollbackStatistics(source);
    check(canonical.statistics0C91FB0C[1]==0x7fc00000u&&canonical.statistics0C91FB0C[2]==0x7fc00000u,"NaN sign/payload differs");
    check(source.statistics0C91FB0C[1]==0xffc00000u&&source.statistics0C91FB0C[2]==0x7fa01234u,"Original published statistics changed");
    check(canonical.statistics0C91FB0C[4]==0xffffffffu,"Integer statistic lane changed");
    for(auto bits:{0u,0x80000000u,0x3f800000u,0xbf800000u,0x7f800000u,0xff800000u}){
        source.statistics0C91FB0C[1]=bits;source.statistics0C91FB0C[2]=bits;
        canonical=canonicalRollbackStatistics(source);
        check(canonical.statistics0C91FB0C[1]==bits&&canonical.statistics0C91FB0C[2]==bits,"Finite value, signed zero or infinity changed");
    }
    source.history0CAA98E0[0]=std::bit_cast<float>(0xffc00000u);
    canonical=canonicalRollbackStatistics(source);
    check(std::bit_cast<unsigned>(canonical.history0CAA98E0[0])==0xffc00000u,"Solver history NaN was normalized");
    std::puts("Rollback statistics checksum checks passed");return 0;
}catch(const std::exception& e){std::fprintf(stderr,"%s\n",e.what());return 1;}}
