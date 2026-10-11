// Test-only separate-process owner of the production online simulation/link.
// A loopback TCP coordinator schedules opaque OnlineRaceLink packets; its
// framed control protocol is deliberately NOT the Unity or Steam transport.
#include "online_race_link.h"
#include "online_race_test_driver.h"
#include "imported_course.h"
#include <bit>
#include <cerrno>
#include <cmath>
#include <iomanip>
#include <iostream>
#include <locale>
#include <sstream>
#include <stdexcept>
#include <string>
#ifdef _WIN32
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <winsock2.h>
#include <ws2tcpip.h>
#include <process.h>
#else
#include <arpa/inet.h>
#include <netinet/tcp.h>
#include <sys/socket.h>
#include <unistd.h>
#endif

using namespace idas3::original;
namespace {
using Bytes=std::vector<std::uint8_t>;
constexpr std::uint32_t maxControlBytes=2*1024*1024;
constexpr std::uint64_t hashSeed=14695981039346656037ull;
void require(bool value,const std::string& why){if(!value)throw std::runtime_error(why);}
void addHash(std::uint64_t& hash,std::uint64_t value){
    for(unsigned i=0;i<8;++i){hash^=(value>>(8*i))&255;hash*=1099511628211ull;}
}
std::string hex(std::uint64_t value){std::ostringstream out;out<<std::hex<<std::setfill('0')<<std::setw(16)<<value;return out.str();}
void put32(Bytes& bytes,std::uint32_t value){for(unsigned i=0;i<4;++i)bytes.push_back(std::uint8_t(value>>(8*i)));}
struct Reader {
    const Bytes& bytes;std::size_t offset=0;
    std::uint32_t get32(){require(offset+4<=bytes.size(),"Truncated harness command");std::uint32_t out=0;
        for(unsigned i=0;i<4;++i)out|=std::uint32_t(bytes[offset++])<<(8*i);
        return out;}
};
#ifdef _WIN32
using Socket=SOCKET;
constexpr Socket invalidSocket=INVALID_SOCKET;
void closeSocket(Socket socket){closesocket(socket);}
unsigned processId(){return unsigned(_getpid());}
struct SocketRuntime {
    SocketRuntime(){WSADATA data{};require(WSAStartup(MAKEWORD(2,2),&data)==0,"WSAStartup failed");}
    ~SocketRuntime(){WSACleanup();}
};
#else
using Socket=int;
constexpr Socket invalidSocket=-1;
void closeSocket(Socket socket){close(socket);}
unsigned processId(){return unsigned(getpid());}
struct SocketRuntime {};
#endif
struct Connection {
    Socket socket=invalidSocket;
    explicit Connection(unsigned port){
        socket=::socket(AF_INET,SOCK_STREAM,IPPROTO_TCP);require(socket!=invalidSocket,"Socket creation failed");
        int enabled=1;
        setsockopt(socket,IPPROTO_TCP,TCP_NODELAY,reinterpret_cast<const char*>(&enabled),sizeof(enabled));
#ifdef _WIN32
        const DWORD timeout=60000;
#else
        const timeval timeout{60,0};
#endif
        setsockopt(socket,SOL_SOCKET,SO_RCVTIMEO,reinterpret_cast<const char*>(&timeout),sizeof(timeout));
        setsockopt(socket,SOL_SOCKET,SO_SNDTIMEO,reinterpret_cast<const char*>(&timeout),sizeof(timeout));
        sockaddr_in destination{};destination.sin_family=AF_INET;destination.sin_port=htons(std::uint16_t(port));
        // No configurable hostname or public bind: this tool only connects to loopback.
        destination.sin_addr.s_addr=htonl(INADDR_LOOPBACK);
        if(connect(socket,reinterpret_cast<sockaddr*>(&destination),sizeof(destination))!=0){closeSocket(socket);socket=invalidSocket;throw std::runtime_error("Cannot connect to loopback coordinator");}
    }
    ~Connection(){if(socket!=invalidSocket)closeSocket(socket);}
    void transfer(std::uint8_t* data,std::size_t size,bool writing){
        while(size){
#ifdef _WIN32
            const int count=writing?send(socket,reinterpret_cast<const char*>(data),int(size),0):recv(socket,reinterpret_cast<char*>(data),int(size),0);
            if(count<0&&WSAGetLastError()==WSAEINTR)continue;
#else
#ifdef MSG_NOSIGNAL
            constexpr int flags=MSG_NOSIGNAL;
#else
            constexpr int flags=0;
#endif
            const auto count=writing?send(socket,data,size,flags):recv(socket,data,size,0);
            if(count<0&&errno==EINTR)continue;
#endif
            require(count>0,writing?"Coordinator send failed":"Coordinator closed or receive timed out");
            data+=count;size-=std::size_t(count);
        }
    }
    void sendFrame(const Bytes& payload){require(payload.size()<=maxControlBytes,"Harness response too large");Bytes wire;put32(wire,std::uint32_t(payload.size()));wire.insert(wire.end(),payload.begin(),payload.end());transfer(wire.data(),wire.size(),true);}
    Bytes receiveFrame(){Bytes header(4);transfer(header.data(),4,false);Reader reader{header};auto count=reader.get32();
        require(count>0&&count<=maxControlBytes,"Harness command size outside bounds");Bytes data(count);transfer(data.data(),data.size(),false);return data;}
};

// Selected observable effects, hashed field-by-field (never struct padding).
// Includes the existing contact oracle plus countdown, rules, impact contents,
// contact completion and engine/tire commands. The state hash separately covers
// the complete production rollback state.
std::uint64_t effectDigest(const OnlineRaceFrame& frame){
    std::uint64_t hash=onlineContactEffectsDigest(frame);
    const auto add=[&](std::uint64_t value){addHash(hash,value);};
    const auto f=[&](float value){add(std::bit_cast<std::uint32_t>(value));};
    const auto& start=frame.start;
    add(start.countdownRemaining);add(std::uint32_t(start.countdownDigit));add(start.go);add(start.gearEnabled);
    add(start.runRules);add(start.cue2);add(start.cue3);add(start.requestService1);
    for(unsigned slot=0;slot<2;++slot){
        const auto& driving=frame.driving[slot];const auto& done=driving.completion;
        add(done.engineChannel);add(done.gear);f(done.engineValue);f(done.engineScale);f(done.throttle);
        add(driving.invalidScalarDiagnostics);add(driving.frame0C92DE30);add(driving.restoredValidRoadState);add(driving.rivalAdvanced);
        for(const auto& impact:driving.newImpactRecords){for(float coordinate:impact.position)f(coordinate);add(impact.tick);f(impact.magnitude);}
        const auto& rule=frame.rules[slot];add(rule.lap);add(rule.section);add(rule.timeExtension);add(rule.finished);add(rule.timeUp);add(std::uint32_t(rule.secondsAdded));
        add(frame.engine[slot].size());for(const auto& command:frame.engine[slot]){add(unsigned(command.target));add(command.handle);add(command.command);add(std::uint32_t(command.value));}
        add(frame.tires[slot].size());for(const auto& command:frame.tires[slot]){add(unsigned(command.type));add(std::uint32_t(command.value));}
    }
    return hash;
}
OnlineRaceSetup setup(unsigned scenario,const std::filesystem::path& imported){
    OnlineRaceSetup selected;selected.condition=scenario%18;selected.wet=selected.condition>=16||(scenario%2)!=0;selected.boost=(scenario%2)!=0;
    for(unsigned slot=0;slot<2;++slot){auto& profile=selected.profiles[slot];profile=makeOriginalFreshBattleProfile();profile.setu(16,(scenario*2+slot)%35);
        if(scenario%3==0)profile.setByte(164,5);
        selected.automatic[slot]=(scenario+slot)%2==0;}
    if(!imported.empty()){
        selected.condition=6+(scenario&1);selected.wet=(scenario&2)!=0;
        selected.imported=std::make_shared<const idas3::ImportedCourse>(idas3::ImportedCourse::load(imported));
    }
    return selected;
}
struct Baseline {
    std::vector<std::array<OriginalVehicleInputs,2>> inputs;
    std::vector<std::uint64_t> states,effects;
    std::uint64_t stateChain=hashSeed,effectChain=hashSeed;
};
Baseline record(const std::filesystem::path& root,const OnlineRaceSetup& selected,unsigned frames){
    OnlineRaceSimulation sim(root,selected);std::array<OriginalHostInputState,2> adapters{};Baseline baseline;
    baseline.inputs.reserve(frames);baseline.states.reserve(frames);baseline.effects.reserve(frames);
    for(unsigned frame=0;frame<frames;++frame){
        std::array<OriginalVehicleInputs,2> input;
        for(unsigned slot=0;slot<2;++slot)input[slot]=adaptOriginalHostInput(adapters[slot],onlineRaceTestControls(sim,slot,frame),selected.automatic[slot],false,frame);
        baseline.inputs.push_back(input);const auto effects=sim.step(input);
        baseline.states.push_back(sim.digest());baseline.effects.push_back(effectDigest(effects));
        addHash(baseline.stateChain,baseline.states.back());addHash(baseline.effectChain,baseline.effects.back());
    }
    return baseline;
}
struct Options {std::filesystem::path root,imported;unsigned port=0,slot=2,frames=1800,scenario=3;std::uint64_t race=726001;};
unsigned integer(const std::string& value,const std::string& key){
    require(!value.empty()&&value.find_first_not_of("0123456789")==std::string::npos,"Invalid "+key);
    const auto result=std::stoull(value);require(result<=1000000000,"Out of range "+key);return unsigned(result);
}
Options arguments(int argc,char** argv){
    Options options;
    for(int i=1;i<argc;++i){const std::string key=argv[i];require(i+1<argc,"Missing value for "+key);const std::string value=argv[++i];
        if(key=="--root")options.root=value;else if(key=="--imported")options.imported=value;
        else if(key=="--port")options.port=integer(value,key);else if(key=="--slot")options.slot=integer(value,key);
        else if(key=="--frames")options.frames=integer(value,key);else if(key=="--scenario")options.scenario=integer(value,key);
        else if(key=="--race")options.race=integer(value,key);else throw std::invalid_argument("Unknown argument: "+key);}
    require(!options.root.empty()&&options.port>0&&options.port<=65535&&options.slot<2,"Required: --root PATH --port 1..65535 --slot 0|1");
    require(options.frames>0&&options.frames<=100000&&options.scenario<18&&options.race>0,"Frames, scenario or race out of range");return options;
}
}
int main(int argc,char** argv)try{
    const auto options=arguments(argc,argv);[[maybe_unused]] const SocketRuntime runtime;Connection connection(options.port);
    const auto selected=setup(options.scenario,options.imported);
    // Each OS process builds its own oracle. Recorded ordinary controls make
    // network variations comparable to exactly the same offline race.
    const auto baseline=record(options.root,selected,options.frames);
    OnlineRaceSimulation simulation(options.root,selected);
    std::array<std::array<float,3>,2> initial{};
    for(unsigned slot=0;slot<2;++slot)for(unsigned i=0;i<3;++i)initial[slot][i]=simulation.car(slot).actor().f(i*4);
    std::uint64_t emitted=0,stateChain=hashSeed,effectsChain=hashSeed,stalls=0,impacts=0;
    OnlineRaceLink link(simulation,options.race,options.slot==0,[&](const OnlineRaceFrame& effect,std::uint64_t digest){
        require(effect.frame==emitted,"Confirmed effects repeated/skipped at frame "+std::to_string(emitted));
        require(emitted<baseline.states.size(),"Confirmed beyond requested race");
        require(digest==baseline.states[emitted],"State differs from offline baseline at frame "+std::to_string(emitted));
        const auto effects=effectDigest(effect);
        require(effects==baseline.effects[emitted],"Effects differ from offline baseline at frame "+std::to_string(emitted));
        for(const auto& driving:effect.driving)impacts+=driving.newImpactRecords.size();
        addHash(stateChain,digest);addHash(effectsChain,effects);++emitted;
    });
    auto status=[&](){
        const auto& timeline=link.timeline();const auto& metrics=timeline.metrics();std::ostringstream out;out.imbue(std::locale::classic());out<<std::setprecision(12);
        out<<"{\"protocol\":1,\"pid\":"<<processId()<<",\"slot\":"<<options.slot<<",\"scenario\":"<<options.scenario<<",\"race\":"<<options.race
           <<",\"frame\":"<<timeline.frame()<<",\"confirmed\":"<<timeline.confirmed()<<",\"verified\":"<<link.verifiedPeerFrames()<<",\"emitted\":"<<emitted
           <<",\"state_digest\":\""<<hex(simulation.digest())<<"\",\"confirmed_digest\":\""<<hex(timeline.confirmedDigest())
           <<"\",\"state_chain\":\""<<hex(stateChain)<<"\",\"effect_chain\":\""<<hex(effectsChain)
           <<"\",\"baseline_final_digest\":\""<<hex(baseline.states.back())<<"\",\"baseline_state_chain\":\""<<hex(baseline.stateChain)
           <<"\",\"baseline_effect_chain\":\""<<hex(baseline.effectChain)<<"\",\"contacts\":"<<simulation.contactFrames()<<",\"impacts\":"<<impacts
           <<",\"stalls\":"<<stalls<<",\"rollbacks\":"<<metrics.rollbacks<<",\"replayed_frames\":"<<metrics.replayedFrames
           <<",\"max_rollback_depth\":"<<metrics.maxDepth<<",\"max_replay_ms\":"<<metrics.maxReplayMs<<",\"max_correction\":"<<metrics.maxCorrection
           <<",\"rejected_batches\":"<<metrics.rejectedBatches<<",\"timeline_memory_bytes\":"<<timeline.memoryBytes()<<",\"cars\":[";
        for(unsigned slot=0;slot<2;++slot){if(slot)out<<',';const auto& rules=simulation.rules(slot).state();double distanceSquared=0;
            for(unsigned i=0;i<3;++i){const float value=simulation.car(slot).actor().f(i*4);require(std::isfinite(value),"Non-finite car pose");const double delta=value-initial[slot][i];distanceSquared+=delta*delta;}
            out<<"{\"progress_index\":"<<rules.progress.index<<",\"progress_fraction\":"<<rules.progress.fraction<<",\"elapsed\":"<<rules.elapsed.value
               <<",\"phase\":"<<unsigned(rules.phase)<<",\"displacement\":"<<std::sqrt(distanceSquared)<<'}';}
        out<<"]}";return out.str();
    };
    auto sendStatus=[&](char kind,bool includePacket){const auto json=status();Bytes response{std::uint8_t(kind)};put32(response,std::uint32_t(json.size()));response.insert(response.end(),json.begin(),json.end());
        if(includePacket){const auto packet=link.packet();response.insert(response.end(),packet.begin(),packet.end());}connection.sendFrame(response);};
    sendStatus('H',false);
    for(;;){
        const auto command=connection.receiveFrame();Reader reader{command};const auto kind=command[reader.offset++];
        if(kind=='Q'){require(command.size()==1,"Malformed quit command");sendStatus('B',false);break;}
        require(kind=='T'&&command.size()>=6,"Unknown/truncated harness command");const auto advance=command[reader.offset++];require(advance<=1,"Invalid advance flag");
        const auto count=reader.get32();require(count<=1024,"Too many packets in harness command");
        for(unsigned i=0;i<count;++i){const auto size=reader.get32();require(size>=40&&size<=1064&&reader.offset+size<=command.size(),"Invalid framed production packet");
            link.receive(std::span(command).subspan(reader.offset,size));reader.offset+=size;link.reconcile();}
        require(reader.offset==command.size(),"Trailing harness command bytes");link.reconcile();
        if(advance&&link.timeline().frame()<options.frames&&!link.step(baseline.inputs.at(link.timeline().frame())[options.slot]))++stalls;
        sendStatus('R',true);
    }
    return 0;
}catch(const std::exception& error){std::cerr<<"headless_online_peer: "<<error.what()<<'\n';return 1;}
