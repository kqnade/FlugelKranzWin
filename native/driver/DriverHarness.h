#pragma once
// Test harness for the driver's hooks. Include it after Driver.cpp. It stands in for
// the parts of vrserver the hooks touch (driver log, settings, device properties and
// the app's shared state) so tests can run the real detours without SteamVR.
#include <map>
#include <string>
#include <vector>
#include <thread>

namespace harness {
struct DriverLog final : vr::IVRDriverLog {
    std::mutex lock;
    std::vector<std::string> lines;
    void Log(const char* message) override {std::lock_guard<std::mutex> guard(lock);lines.emplace_back(message?message:"");}
    bool contains(const char* text) {
        std::lock_guard<std::mutex> guard(lock);
        for(const auto& line:lines) if(line.find(text)!=std::string::npos) return true;
        return false;
    }
    size_t count(const char* text) {
        std::lock_guard<std::mutex> guard(lock);
        size_t found=0;
        for(const auto& line:lines) if(line.find(text)!=std::string::npos) found++;
        return found;
    }
    std::string last(const char* text) {
        std::lock_guard<std::mutex> guard(lock);
        for(auto line=lines.rbegin();line!=lines.rend();++line) if(line->find(text)!=std::string::npos) return *line;
        return {};
    }
    void clear() {std::lock_guard<std::mutex> guard(lock);lines.clear();}
};

// Only the driver_flugelkranz section is modelled. A key that was never set reports
// VRSettingsError_UnsetSettingHasNoDefault, like a key missing from every settings file.
struct Settings final : vr::IVRSettings {
    std::map<std::string,bool> bools;
    std::map<std::string,std::string> strings;
    const char* GetSettingsErrorNameFromEnum(vr::EVRSettingsError) override {return "";}
    void SetBool(const char*,const char* key,bool value,vr::EVRSettingsError* error) override {bools[key]=value;if(error)*error=vr::VRSettingsError_None;}
    void SetInt32(const char*,const char*,int32_t,vr::EVRSettingsError* error) override {if(error)*error=vr::VRSettingsError_None;}
    void SetFloat(const char*,const char*,float,vr::EVRSettingsError* error) override {if(error)*error=vr::VRSettingsError_None;}
    void SetString(const char*,const char* key,const char* value,vr::EVRSettingsError* error) override {strings[key]=value;if(error)*error=vr::VRSettingsError_None;}
    bool GetBool(const char*,const char* key,vr::EVRSettingsError* error) override {
        auto found=bools.find(key);
        if(error)*error=found==bools.end()?vr::VRSettingsError_UnsetSettingHasNoDefault:vr::VRSettingsError_None;
        return found!=bools.end() && found->second;
    }
    int32_t GetInt32(const char*,const char*,vr::EVRSettingsError* error) override {if(error)*error=vr::VRSettingsError_UnsetSettingHasNoDefault;return 0;}
    float GetFloat(const char*,const char*,vr::EVRSettingsError* error) override {if(error)*error=vr::VRSettingsError_UnsetSettingHasNoDefault;return 0;}
    void GetString(const char*,const char* key,char* value,uint32_t length,vr::EVRSettingsError* error) override {
        auto found=strings.find(key);
        if(found==strings.end()) {if(length)value[0]=0;if(error)*error=vr::VRSettingsError_UnsetSettingHasNoDefault;return;}
        if(length) {std::strncpy(value,found->second.c_str(),length-1);value[length-1]=0;}
        if(error)*error=vr::VRSettingsError_None;
    }
    void RemoveSection(const char*,vr::EVRSettingsError* error) override {if(error)*error=vr::VRSettingsError_None;}
    void RemoveKeyInSection(const char*,const char*,vr::EVRSettingsError* error) override {if(error)*error=vr::VRSettingsError_None;}
};

// Prop_TrackingSystemName_String per device index; other properties are unknown.
struct Properties final : vr::IVRProperties {
    std::map<uint32_t,std::string> trackingSystems;
    vr::ETrackedPropertyError ReadPropertyBatch(vr::PropertyContainerHandle_t container,vr::PropertyRead_t* batch,uint32_t count) override {
        for(uint32_t i=0;i<count;i++) {
            auto& read=batch[i];
            auto found=trackingSystems.find(static_cast<uint32_t>(container-1));
            if(read.prop!=vr::Prop_TrackingSystemName_String || found==trackingSystems.end()) {read.eError=vr::TrackedProp_UnknownProperty;read.unTag=vr::k_unInvalidPropertyTag;read.unRequiredBufferSize=0;continue;}
            const auto size=static_cast<uint32_t>(found->second.size()+1);
            read.unTag=vr::k_unStringPropertyTag;read.unRequiredBufferSize=size;
            if(read.unBufferSize<size) {read.eError=vr::TrackedProp_BufferTooSmall;continue;}
            std::memcpy(read.pvBuffer,found->second.c_str(),size);
            read.eError=vr::TrackedProp_Success;
        }
        return vr::TrackedProp_Success;
    }
    vr::ETrackedPropertyError WritePropertyBatch(vr::PropertyContainerHandle_t,vr::PropertyWrite_t*,uint32_t) override {return vr::TrackedProp_Success;}
    const char* GetPropErrorNameFromEnum(vr::ETrackedPropertyError) override {return "";}
    vr::PropertyContainerHandle_t TrackedDeviceToPropertyContainer(vr::TrackedDeviceIndex_t device) override {return device+1;}
};

struct Context final : vr::IVRDriverContext {
    DriverLog log;Settings settings;Properties properties;
    void* GetGenericInterface(const char* version,vr::EVRInitError* error) override {
        void* result=nullptr;
        if(std::strcmp(version,vr::IVRDriverLog_Version)==0) result=static_cast<vr::IVRDriverLog*>(&log);
        else if(std::strcmp(version,vr::IVRSettings_Version)==0) result=static_cast<vr::IVRSettings*>(&settings);
        else if(std::strcmp(version,vr::IVRProperties_Version)==0) result=static_cast<vr::IVRProperties*>(&properties);
        if(error)*error=result?vr::VRInitError_None:vr::VRInitError_Init_InterfaceNotFound;
        return result;
    }
    vr::DriverHandle_t GetDriverHandle() override {return 1;}
};

inline Context& context() {static Context instance;return instance;}

// Route vr::VRDriverLog(), vr::VRSettings() and vr::VRProperties() to the stubs.
inline void install() {
    vr::VRDriverContext()=&context();
    vr::OpenVRInternal_ModuleServerDriverContext().Clear();
}

// The settings shipped in resources/settings/default.vrsettings.
inline void shippedDefaults() {
    auto& settings=context().settings;
    settings.bools={{"observeFrames",true},{"correctFramePose",true},{"timeBasedFramePose",true}};
    settings.strings={{"hmdAnchoredTrackingSystems",""},{"physicalPoseDrivers","driver_slimevr.dll,driver_rebocap.dll"}};
}

// A real IPC mutex and shared state, as the app would map them.
inline flight::State& ipcState() {static flight::State instance;return instance;}
inline void useIpc() {
    if(!ipcMutex) ipcMutex=CreateMutexW(nullptr,FALSE,nullptr);
    new(&ipcState()) flight::State{};
    state=&ipcState();
}
// The app side: publish a command with a fresh heartbeat, as DriverConnection does.
inline void publish(flight::Pose transform,bool enabled=true) {
    WaitForSingleObject(ipcMutex,INFINITE);
    state->owner=1;state->enabled=enabled?1:0;
    state->clientHeartbeat=GetTickCount64();
    state->command=transform;
    ReleaseMutex(ipcMutex);
}
// Holds the IPC mutex from another thread (a Windows mutex is recursive for its owner),
// as the app does while it is paused inside its lock.
class AppHoldsIpc {
    HANDLE acquired=CreateEventW(nullptr,TRUE,FALSE,nullptr),release=CreateEventW(nullptr,TRUE,FALSE,nullptr);
    std::thread holder;
public:
    AppHoldsIpc():holder([this]{WaitForSingleObject(ipcMutex,INFINITE);SetEvent(acquired);WaitForSingleObject(release,INFINITE);ReleaseMutex(ipcMutex);}) {
        WaitForSingleObject(acquired,INFINITE);
    }
    ~AppHoldsIpc() {SetEvent(release);holder.join();CloseHandle(acquired);CloseHandle(release);}
};

// The test executable's own file name, so rawPoses() treats the test as a solver.
inline std::string executableName() {
    wchar_t path[MAX_PATH]{};
    const DWORD length=GetModuleFileNameW(nullptr,path,MAX_PATH);
    const wchar_t* file=path;
    for(DWORD i=0;i<length;i++) if(path[i]==L'\\' || path[i]==L'/') file=path+i+1;
    char name[MAX_PATH]{};
    WideCharToMultiByte(CP_UTF8,0,file,-1,name,sizeof(name),nullptr,nullptr);
    return name;
}
inline void forgetCallSites() {
    for(auto& site:physicalCallSites) site.store(nullptr);
    for(auto& site:flownCallSites) site.store(nullptr);
}
inline void servePhysicalPosesToThisProcess(bool physical=true) {
    const auto name=physical?executableName():std::string("driver_slimevr.dll");
    std::snprintf(physicalPoseDrivers,sizeof(physicalPoseDrivers),"%s",name.c_str());
    forgetCallSites();
}
}
