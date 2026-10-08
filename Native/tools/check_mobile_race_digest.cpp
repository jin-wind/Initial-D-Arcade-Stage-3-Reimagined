// Run the same fixed input fixture on Android and iOS Simulator, then compare
// the integer words. No rendering, networking, save writes or ROM are needed.
#include "online_race_simulation.h"
#include "original_host_input.h"
#include <cstdio>
#include <cstring>
#include <cstdlib>
#include <stdexcept>
#include <type_traits>

using namespace idas3::original;
template<class T> void dump(unsigned frame,unsigned slot,const char* group,const T& value){
    static_assert(std::is_trivially_copyable_v<T> && sizeof(T)%4==0);
    for(unsigned i=0;i<sizeof(T)/4;++i){
        std::uint32_t word;std::memcpy(&word,reinterpret_cast<const char*>(&value)+i*4,4);
        std::printf("%u,%u,%s,%u,%08x\n",frame,slot,group,i,word);
    }
}
int main(int argc,char** argv){try{
    if(argc<2||argc>4)throw std::invalid_argument("Usage: check_mobile_race_digest <root containing data/> [condition 0..17] [frames]");
    unsigned condition=argc>2?unsigned(std::stoul(argv[2])):0;
    unsigned frames=argc>3?unsigned(std::stoul(argv[3])):600;
    if(condition>=18||frames==0||frames>20000)throw std::invalid_argument("Fixture argument outside bounds");
    OnlineRaceSetup setup;setup.condition=condition;setup.boost=true;setup.wet=condition>=16||condition%3==2;
    for(unsigned slot=0;slot<2;++slot){setup.profiles[slot]=makeOriginalFreshBattleProfile();setup.profiles[slot].setu(16,slot==0?0:8);}
    setup.profiles[0].setByte(164,5);
    OnlineRaceSimulation sim(argv[1],setup);
    std::array<OriginalHostInputState,2> host{};
    for(unsigned frame=0;frame<=frames;++frame){
        std::printf("%u,2,simulation,0,%016llx\n",frame,(unsigned long long)sim.digest());
        for(unsigned slot=0;slot<2;++slot){
            const auto& car=sim.car(slot);const auto& vehicle=car.vehicle();
            std::printf("%u,%u,rollback,0,%016llx\n",frame,slot,(unsigned long long)car.rollbackDigest());
            if(frame<=4){
                dump(frame,slot,"actor",car.actor());dump(frame,slot,"drive",vehicle.drive);
                dump(frame,slot,"loss",vehicle.loss);dump(frame,slot,"tail",vehicle.tail);
                dump(frame,slot,"transmission",vehicle.transmission);
                dump(frame,slot,"transmissionGlobals",vehicle.transmissionGlobals);
                dump(frame,slot,"controls",vehicle.controls);dump(frame,slot,"published",car.publishedActors());
                dump(frame,slot,"wheels",car.wheelHistory());dump(frame,slot,"completion",car.contactCompletion());
                dump(frame,slot,"initialization",car.initializationSide());
            }
        }
        if(frame==frames)break;
        std::array<OriginalVehicleInputs,2> input;
        for(unsigned slot=0;slot<2;++slot){
            OriginalHostControls physical;
            if(frame>=200)physical.throttle=1;
            if(frame>=400)physical.steering=slot==0?.1f:-.1f;
            input[slot]=adaptOriginalHostInput(host[slot],physical,true,false,frame);
        }
        sim.step(input);
    }
    return 0;
}catch(const std::exception& error){std::fprintf(stderr,"%s\n",error.what());return 1;}}
