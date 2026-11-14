// 実manifestに登録されたWinRTクラスを、公開IActivationFactory/IInspectableだけで検査。
// 未知のIID・メソッドのABIは推測して呼ばない。UIA・フック・秘密値取得なし。
#include <Windows.h>
#include <roapi.h>
#include <activation.h>
#include <inspectable.h>
#include <winstring.h>
#include <filesystem>
#include <iostream>
#include <iomanip>
#include <string>
#include <atomic>
#include <mutex>
#include <deque>
#include <mmsystem.h>
#include <speechapi_cxx.h>
#include <condition_variable>
#include <fstream>
#include <vector>
#include <bcrypt.h>
#pragma comment(lib,"winmm.lib")
#pragma comment(lib,"bcrypt.lib")

static std::string fileHash(const std::filesystem::path& path) {
    std::ifstream file(path,std::ios::binary);if(!file)return "";
    std::vector<unsigned char> data((std::istreambuf_iterator<char>(file)),{});
    unsigned char digest[32]{};
    if(data.size()>0xffffffffUL || BCryptHash(BCRYPT_SHA256_ALG_HANDLE,nullptr,0,data.data(),static_cast<ULONG>(data.size()),digest,32)<0)return "";
    std::ostringstream hex;for(auto b:digest)hex<<std::hex<<std::setfill('0')<<std::setw(2)<<static_cast<unsigned>(b);
    return hex.str();
}
static bool knownHash(const std::filesystem::path& path,const char* expected) {return fileHash(path)==expected;}

// 自作プロセスが正規QueryInterfaceで得たオブジェクトだけを調べる。
extern "C" __declspec(dllexport) void* ResearchBackendInterface = nullptr;

template<class Fn> static Fn checkedMethod(void* instance,HMODULE module,size_t slot,uintptr_t rva) {
    auto entry=(*reinterpret_cast<void***>(instance))[slot];
    if(reinterpret_cast<uintptr_t>(entry)-reinterpret_cast<uintptr_t>(module)!=rva) throw std::runtime_error("Backend ABI mismatch");
    return reinterpret_cast<Fn>(entry);
}
// 2026-10-11の正規QIとMicrosoft公開PDBで確認したx64 ABI。未知の版では停止する。
using NoArg=HRESULT(STDMETHODCALLTYPE*)(void*);
using GetBool=HRESULT(STDMETHODCALLTYPE*)(void*,bool*);
using SetBool=HRESULT(STDMETHODCALLTYPE*)(void*,bool);
using GetString=HRESULT(STDMETHODCALLTYPE*)(void*,HSTRING*);
using AddEvent=HRESULT(STDMETHODCALLTYPE*)(void*,IUnknown*,INT64*);
using RemoveEvent=HRESULT(STDMETHODCALLTYPE*)(void*,INT64);
class CaptionSink final:public IUnknown {
    std::atomic<ULONG> refs{1};
public:
    std::mutex mutex;
    struct Event {IInspectable* args; ULONGLONG observedTick;};
    std::deque<Event> queue;
    std::atomic<bool> overflow=false;
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id,void** value) override {
        if(!value)return E_POINTER; *value=nullptr;
        if(id==__uuidof(IUnknown)||id==__uuidof(IAgileObject)){*value=static_cast<IUnknown*>(this);AddRef();return S_OK;}
        return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() override {return ++refs;}
    ULONG STDMETHODCALLTYPE Release() override {auto n=--refs;if(!n)delete this;return n;}
    // TypedEventHandler<CaptionBackend,CaptionRecognitionEventArgs>::Invoke(void*,void*)。
    virtual HRESULT STDMETHODCALLTYPE Invoke(IInspectable*,IInspectable* args) {
        if(!args)return E_POINTER;
        try {
            std::lock_guard<std::mutex> lock(mutex);
            if(queue.size()>=4096){overflow=true;return E_OUTOFMEMORY;}
            queue.push_back({args,GetTickCount64()});args->AddRef();return S_OK;
        } catch(...) {overflow=true;return E_OUTOFMEMORY;}
    }
    ~CaptionSink(){for(auto& item:queue)item.args->Release();}
};
static std::string utf8(const std::wstring& value) {
    int n=WideCharToMultiByte(CP_UTF8,0,value.data(),static_cast<int>(value.size()),nullptr,0,nullptr,nullptr);
    std::string result(n,'\0');WideCharToMultiByte(CP_UTF8,0,value.data(),static_cast<int>(value.size()),result.data(),n,nullptr,nullptr);return result;
}
static std::string quote(const std::string& text) {
    std::string result="\"";
    for(unsigned char c:text){if(c=='"'||c=='\\'){result+='\\';result+=c;}else if(c<32){char b[7];sprintf_s(b,"\\u%04x",c);result+=b;}else result+=c;}
    return result+'"';
}

static void hr(const wchar_t* name,HRESULT result) {
    std::cout << utf8(name) << "=0x" << std::hex << static_cast<unsigned>(result) << std::dec << std::endl;
}
// 正規に生成した自作Backendに設定生成を任せる。設定内容・モデルキーは読まず保存しない。
// privateメソッドのRVAとthis調整は公開PDBとStartのABI thunkで照合済み。
static std::shared_ptr<Microsoft::CognitiveServices::Speech::EmbeddedSpeechConfig> createFileConfig(void* object,HMODULE module) {
    using namespace Microsoft::CognitiveServices::Speech;
    auto r=checkedMethod<NoArg>(object,module,30,0x33690)(object);hr(L"UpdateSpeechModelLocaleFromSettings",r);if(FAILED(r))throw std::runtime_error("model locale unavailable");
    HSTRING language=nullptr;
    r=checkedMethod<GetString>(object,module,31,0x2dee0)(object,&language);
    std::wstring current=language?WindowsGetStringRawBuffer(language,nullptr):L"";
    if(language)WindowsDeleteString(language);
    if(FAILED(r)||current!=L"ja-JP")throw std::runtime_error("ja-JP model not ready");
    checkedMethod<NoArg>(object,module,6,0x31f30); // this=-8を確認したthunkの版を検査
    std::shared_ptr<EmbeddedSpeechConfig> config;
    using GetConfig=void*(*)(void*,std::shared_ptr<EmbeddedSpeechConfig>*);
    auto getConfig=reinterpret_cast<GetConfig>(reinterpret_cast<uintptr_t>(module)+0x2e568);
    getConfig(static_cast<char*>(object)-8,&config);
    if(!config)throw std::runtime_error("embedded configuration missing");
    std::cout<<"BackendConfig=created (contents not read)"<<std::endl;
    return config;
}
static HRESULT runFile(void* object,HMODULE module,const wchar_t* wave,bool pcm) {
    using namespace Microsoft::CognitiveServices::Speech;
    auto config=createFileConfig(object,module);
    if(!wave){std::cout<<"{\"kind\":\"ready\",\"ready\":true}"<<std::endl;return S_OK;}
    std::shared_ptr<Audio::PushAudioInputStream> stream;
    std::shared_ptr<Audio::AudioConfig> audio;
    if(pcm){stream=Audio::AudioInputStream::CreatePushStream(Audio::AudioStreamFormat::GetWaveFormatPCM(16000,16,1));audio=Audio::AudioConfig::FromStreamInput(stream);}
    else audio=Audio::AudioConfig::FromWavFileInput(utf8(wave));
    auto recognizer=SpeechRecognizer::FromConfig(config,audio);
    std::mutex mutex;std::condition_variable signal;bool done=false,failed=false;size_t finals=0,totalText=0;
    struct Result {std::string text;bool final;ULONGLONG tick;};std::vector<Result> results;
    auto emit=[&](const SpeechRecognitionEventArgs& args,bool final){
        std::lock_guard<std::mutex> lock(mutex);
        if(results.size()>=8192 || totalText+args.Result->Text.size()>4000000){failed=true;return;}
        if(final&&!args.Result->Text.empty())++finals;
        totalText+=args.Result->Text.size();results.push_back({args.Result->Text,final,GetTickCount64()});
    };
    recognizer->Recognizing.Connect([&](const SpeechRecognitionEventArgs& args){emit(args,false);});
    recognizer->Recognized.Connect([&](const SpeechRecognitionEventArgs& args){emit(args,true);});
    recognizer->SessionStopped.Connect([&](const SessionEventArgs&){std::lock_guard<std::mutex> lock(mutex);done=true;signal.notify_all();});
    int cancelReason=0,cancelError=0;
    recognizer->Canceled.Connect([&](const SpeechRecognitionCanceledEventArgs& args){std::lock_guard<std::mutex> lock(mutex);failed=failed||args.Reason==CancellationReason::Error;cancelReason=static_cast<int>(args.Reason);cancelError=static_cast<int>(args.ErrorCode);done=true;signal.notify_all();});
    auto started=std::chrono::steady_clock::now();
    recognizer->StartContinuousRecognitionAsync().get();
    if(pcm) {
        std::ifstream file(std::filesystem::path(wave),std::ios::binary);
        if(!file)throw std::runtime_error("PCM file missing");
        std::vector<uint8_t> buffer(32000);while(file){file.read(reinterpret_cast<char*>(buffer.data()),buffer.size());auto n=file.gcount();if(n>0)stream->Write(buffer.data(),static_cast<uint32_t>(n));}
        stream->Close();
    }
    {std::unique_lock<std::mutex> lock(mutex);if(!signal.wait_for(lock,std::chrono::seconds(60),[&]{return done;}))failed=true;}
    recognizer->StopContinuousRecognitionAsync().get();
    auto elapsed=std::chrono::duration<double>(std::chrono::steady_clock::now()-started).count();
    recognizer->Recognizing.DisconnectAll();recognizer->Recognized.DisconnectAll();recognizer->SessionStopped.DisconnectAll();recognizer->Canceled.DisconnectAll();
    size_t sequence=0;for(const auto& result:results)
        std::cout<<"{\"kind\":\"backend_recognition\",\"sequence\":"<<++sequence<<",\"observed_tick_ms\":"<<result.tick<<",\"is_final\":"<<(result.final?"true":"false")<<",\"text\":"<<quote(result.text)<<"}"<<std::endl;
    std::cout<<"{\"kind\":\"summary\",\"recognition_seconds\":"<<elapsed<<",\"success\":"<<(!failed&&finals?"true":"false")<<",\"final_count\":"<<finals<<",\"cancel_reason\":"<<cancelReason<<",\"cancel_error\":"<<cancelError<<"}"<<std::endl;
    return !failed&&finals?S_OK:E_FAIL;
}
// ファイル音源はEOFを保持するため、同じ認識器へ再設定できるPull入力を使用する。
class SessionWaveInput final:public Microsoft::CognitiveServices::Speech::Audio::PullAudioInputStreamCallback {
public:
    std::vector<uint8_t> data;size_t consumed=0;
    void Load(const wchar_t* path) {
        std::ifstream file(std::filesystem::path(path),std::ios::binary);
        std::vector<uint8_t> wave((std::istreambuf_iterator<char>(file)),{});
        auto u32=[&](size_t i){if(i+4>wave.size())throw std::runtime_error("invalid WAV");return static_cast<uint32_t>(wave[i])|(static_cast<uint32_t>(wave[i+1])<<8)|(static_cast<uint32_t>(wave[i+2])<<16)|(static_cast<uint32_t>(wave[i+3])<<24);};
        if(wave.size()<44 || wave.size()>9600100 || std::string(wave.begin(),wave.begin()+4)!="RIFF" || std::string(wave.begin()+8,wave.begin()+12)!="WAVE")throw std::runtime_error("invalid session WAV");
        bool format=false,found=false;
        for(size_t offset=12;offset+8<=wave.size();) {
            auto size=u32(offset+4);size_t begin=offset+8;if(size>wave.size()-begin)throw std::runtime_error("WAV chunk exceeds input");
            auto name=std::string(wave.begin()+offset,wave.begin()+offset+4);
            if(name=="fmt "){if(size<16 || wave[begin]!=1 || wave[begin+1]!=0 || wave[begin+2]!=1 || wave[begin+3]!=0 || u32(begin+4)!=16000 || wave[begin+14]!=16 || wave[begin+15]!=0)throw std::runtime_error("16kHz mono PCM required");format=true;}
            if(name=="data"){if(size%2)throw std::runtime_error("odd PCM length");data.assign(wave.begin()+begin,wave.begin()+begin+size);found=true;}
            offset=begin+size+(size%2);
        }
        if(!format||!found||data.empty())throw std::runtime_error("PCM chunk missing");consumed=0;
    }
    int Read(uint8_t* buffer,uint32_t size) override {
        auto count=(std::min)(static_cast<size_t>(size),data.size()-consumed);
        std::copy(data.begin()+consumed,data.begin()+consumed+count,buffer);consumed+=count;return static_cast<int>(count);
    }
    void Close() override {} // セッションのEOFではメモリを破棄せず、停止後に次入力へ置換する。
};
// ベンチマーク専用。stdinは連番、最後はquit。親は停止後だけ固定WAVを置換する。
static HRESULT runFileSession(void* object,HMODULE module,const wchar_t* wave) {
    using namespace Microsoft::CognitiveServices::Speech;
    auto loadedAt=std::chrono::steady_clock::now();
    auto config=createFileConfig(object,module);
    auto input=std::make_shared<SessionWaveInput>();
    auto stream=Audio::AudioInputStream::CreatePullStream(Audio::AudioStreamFormat::GetWaveFormatPCM(16000,16,1),input);
    auto audio=Audio::AudioConfig::FromStreamInput(stream);
    auto recognizer=SpeechRecognizer::FromConfig(config,audio);
    std::mutex mutex;std::condition_variable signal;
    bool done=false,failed=false,accepting=false;size_t finals=0,totalText=0;
    int cancelReason=0,cancelError=0;
    struct Result {std::string text;bool final;ULONGLONG tick;};std::vector<Result> results;
    auto emit=[&](const SpeechRecognitionEventArgs& args,bool final){
        std::lock_guard<std::mutex> lock(mutex);
        if(!accepting){failed=true;return;}
        if(results.size()>=8192 || totalText+args.Result->Text.size()>4000000){failed=true;return;}
        if(final&&!args.Result->Text.empty())++finals;
        totalText+=args.Result->Text.size();results.push_back({args.Result->Text,final,GetTickCount64()});
    };
    recognizer->Recognizing.Connect([&](const SpeechRecognitionEventArgs& args){emit(args,false);});
    recognizer->Recognized.Connect([&](const SpeechRecognitionEventArgs& args){emit(args,true);});
    recognizer->SessionStopped.Connect([&](const SessionEventArgs&){std::lock_guard<std::mutex> lock(mutex);done=true;signal.notify_all();});
    recognizer->Canceled.Connect([&](const SpeechRecognitionCanceledEventArgs& args){std::lock_guard<std::mutex> lock(mutex);failed=failed||args.Reason==CancellationReason::Error;cancelReason=static_cast<int>(args.Reason);cancelError=static_cast<int>(args.ErrorCode);done=true;signal.notify_all();});
    struct Disconnect {
        std::shared_ptr<SpeechRecognizer>& value;
        ~Disconnect(){if(value){value->Recognizing.DisconnectAll();value->Recognized.DisconnectAll();value->SessionStopped.DisconnectAll();value->Canceled.DisconnectAll();try{value->StopContinuousRecognitionAsync().get();}catch(...){}value.reset();}}
    } disconnect{recognizer};
    auto recognize=[&](){
        input->Load(wave);
        {std::lock_guard<std::mutex> lock(mutex);if(failed)throw std::runtime_error("session no longer reusable");done=false;accepting=true;finals=0;totalText=0;cancelReason=0;cancelError=0;results.clear();}
        auto started=std::chrono::steady_clock::now();
        recognizer->StartContinuousRecognitionAsync().get();
        {std::unique_lock<std::mutex> lock(mutex);if(!signal.wait_for(lock,std::chrono::seconds(60),[&]{return done;}))failed=true;}
        recognizer->StopContinuousRecognitionAsync().get();
        auto elapsed=std::chrono::duration<double>(std::chrono::steady_clock::now()-started).count();
        {std::lock_guard<std::mutex> lock(mutex);accepting=false;if(failed)throw std::runtime_error("resident recognition failed");}
        if(input->consumed!=input->data.size())throw std::runtime_error("session did not consume whole PCM input");
        return elapsed;
    };
    // 親が置いた無音WAVだけを処理し、遅延初期化もロード区間へ含める。
    recognize();
    if(finals)throw std::runtime_error("warmup input must be silent");
    auto loadSeconds=std::chrono::duration<double>(std::chrono::steady_clock::now()-loadedAt).count();
    std::cout<<"{\"kind\":\"session_ready\",\"load_seconds\":"<<loadSeconds<<",\"recognizer_instance\":1,\"warmup_silence\":true}"<<std::endl;
    std::string line;size_t expected=1;bool quit=false;
    while(std::getline(std::cin,line)) {
        if(line=="quit"){quit=true;break;}
        if(expected>1000 || line!=std::to_string(expected))throw std::runtime_error("invalid session request");
        auto hash=fileHash(wave);if(hash.empty())throw std::runtime_error("input hash missing");
        auto seconds=recognize();
        for(const auto& result:results)
            std::cout<<"{\"kind\":\"backend_recognition\",\"request\":"<<expected<<",\"observed_tick_ms\":"<<result.tick<<",\"is_final\":"<<(result.final?"true":"false")<<",\"text\":"<<quote(result.text)<<"}"<<std::endl;
        std::cout<<"{\"kind\":\"session_result\",\"request\":"<<expected<<",\"input_sha256\":"<<quote(hash)<<",\"pcm_consumed_bytes\":"<<input->consumed<<",\"recognition_seconds\":"<<seconds<<",\"success\":"<<(finals?"true":"false")<<",\"final_count\":"<<finals<<",\"recognizer_instance\":1,\"cancel_reason\":"<<cancelReason<<",\"cancel_error\":"<<cancelError<<"}"<<std::endl;
        ++expected;
    }
    recognizer->Recognizing.DisconnectAll();recognizer->Recognized.DisconnectAll();recognizer->SessionStopped.DisconnectAll();recognizer->Canceled.DisconnectAll();
    recognizer.reset();audio.reset();config.reset();
    std::cout<<"{\"kind\":\"session_closed\",\"requests\":"<<expected-1<<",\"disposed\":true}"<<std::endl;
    return quit?S_OK:E_FAIL;
}
static HRESULT runBackend(void* object,HMODULE module,const wchar_t* wave) {
    auto start=checkedMethod<NoArg>(object,module,6,0x31f30);
    auto stop=checkedMethod<NoArg>(object,module,7,0x326b0);
    auto add=checkedMethod<AddEvent>(object,module,10,0x355d0);
    auto remove=checkedMethod<RemoveEvent>(object,module,11,0x39770);
    auto available=checkedMethod<GetBool>(object,module,21,0x2f320);
    auto locale=checkedMethod<GetString>(object,module,31,0x2dee0);
    auto getMic=checkedMethod<GetBool>(object,module,32,0x37f70);
    auto setMic=checkedMethod<SetBool>(object,module,33,0x393f0);
    bool ready=false,mic=false; HSTRING language=nullptr;
    auto r=checkedMethod<NoArg>(object,module,30,0x33690)(object);hr(L"UpdateSpeechModelLocaleFromSettings",r);if(FAILED(r))return r;
    r=available(object,&ready);hr(L"IsSpeechModelAvailable",r);std::cout<<"ModelAvailable="<<ready<<std::endl;if(FAILED(r)||!ready)return HRESULT_FROM_WIN32(ERROR_NOT_READY);
    r=locale(object,&language);hr(L"GetCurrentSpeechModelLocale",r);
    std::wstring currentLocale=language?WindowsGetStringRawBuffer(language,nullptr):L"";
    if(language)WindowsDeleteString(language);
    std::cout<<"Locale="<<utf8(currentLocale)<<std::endl;
    if(FAILED(r)||currentLocale!=L"ja-JP")return HRESULT_FROM_WIN32(ERROR_NOT_READY);
    r=getMic(object,&mic);hr(L"get_MicrophoneOn",r);if(FAILED(r))return r;
    r=setMic(object,false);hr(L"put_MicrophoneOn(false)",r);if(FAILED(r))return r;
    auto sink=new CaptionSink();INT64 token=0;
    r=add(object,sink,&token);hr(L"add_CaptionRecognition",r);
    if(FAILED(r)){setMic(object,mic);sink->Release();return r;}
    r=start(object);hr(L"Start",r);
    if(SUCCEEDED(r)) {
        Sleep(5000);
        std::cout<<"PLAYBACK_START="<<GetTickCount64()<<std::endl;
        bool played=PlaySoundW(wave,nullptr,SND_FILENAME|SND_SYNC|SND_NODEFAULT)!=FALSE;
        std::cout<<"PLAYBACK_END="<<GetTickCount64()<<" success="<<played<<std::endl;
        Sleep(3000);
        r=stop(object);hr(L"Stop",r);
        if(!played)r=HRESULT_FROM_WIN32(ERROR_READ_FAULT);
    }
    auto removed=remove(object,token);hr(L"remove_CaptionRecognition",removed);if(FAILED(removed))r=removed;
    auto restored=setMic(object,mic);hr(L"restore_MicrophoneOn",restored);if(FAILED(restored))r=restored;
    size_t count=0,finalCount=0;
    {
        std::lock_guard<std::mutex> lock(sink->mutex);
        for(auto& item:sink->queue) {
            auto* args=item.args;
            HSTRING value=nullptr;bool final=false;
            auto textHr=checkedMethod<GetString>(args,module,6,0x58b40)(args,&value);
            auto finalHr=checkedMethod<GetBool>(args,module,7,0x58b00)(args,&final);
            if(SUCCEEDED(textHr)&&SUCCEEDED(finalHr)) {
                auto text=utf8(WindowsGetStringRawBuffer(value,nullptr));
                if(final&&!text.empty())++finalCount;
                std::cout<<"{\"kind\":\"backend_recognition\",\"sequence\":"<<++count<<",\"observed_tick_ms\":"<<item.observedTick<<",\"is_final\":"<<(final?"true":"false")<<",\"text\":"<<quote(text)<<"}"<<std::endl;
            }
            if(value)WindowsDeleteString(value);
        }
    }
    std::cout<<"Events="<<count<<" finals="<<finalCount<<" overflow="<<sink->overflow<<std::endl;
    if(sink->overflow||!finalCount)r=HRESULT_FROM_WIN32(ERROR_NO_DATA);
    sink->Release();return r;
}
int wmain(int argc,wchar_t** argv) {
    if(argc<2 || argc>4){std::cerr<<"BackendProbe <absolute Windows Backend DLL> [--inspect-abi | --run WAV | --file WAV16k | --pcm PCM16k]\n";return 64;}
    bool inspect=argc==3 && std::wstring(argv[2])==L"--inspect-abi";
    bool run=argc==4 && std::wstring(argv[2])==L"--run";
    bool pcm=argc==4 && std::wstring(argv[2])==L"--pcm";
    bool session=argc==4 && std::wstring(argv[2])==L"--session";
    bool status=argc==3 && std::wstring(argv[2])==L"--status";
    bool file=status || (argc==4 && (std::wstring(argv[2])==L"--file"||pcm));
    if(argc>2 && !inspect && !run && !file && !session) return 64;
    auto dll=std::filesystem::weakly_canonical(argv[1]);
    wchar_t windows[MAX_PATH]; GetWindowsDirectoryW(windows,MAX_PATH);
    std::wstring prefix=std::wstring(windows)+L"\\SystemApps\\";
    if(!dll.is_absolute() || _wcsnicmp(dll.c_str(),prefix.c_str(),prefix.size())!=0 || dll.filename()!=L"LiveCaptionsBackendDll.dll") return 64;
    if(!knownHash(dll,"eccd4adc7e4e8f127344a0ccbb7e09c92e185d8589cf5197a6f0dd1de0ce9fef") ||
       !knownHash(dll.parent_path()/L"LiveCaptions"/L"Microsoft.CognitiveServices.Speech.core.dll","00b8c930a414ab37148beb359adbcecf828ef7e9dd4452dc1dde378a6a5fd3bd")) {
        std::cerr<<"Unsupported Windows DLL version. ABI has not been verified.\n";return 65;
    }
    HRESULT r=RoInitialize(RO_INIT_MULTITHREADED); hr(L"RoInitialize",r); if(FAILED(r)) return 2;
    SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_SYSTEM32|LOAD_LIBRARY_SEARCH_USER_DIRS);
    auto c1=AddDllDirectory(dll.parent_path().c_str());
    auto sdk=dll.parent_path()/L"LiveCaptions"; auto c2=AddDllDirectory(sdk.c_str());
    auto runtime=std::wstring(windows)+L"\\SystemApps\\MicrosoftWindows.Client.CoreAI_cw5n1h2txyewy";
    auto c3=AddDllDirectory(runtime.c_str());
    HMODULE backend=LoadLibraryExW(dll.c_str(),nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32|LOAD_LIBRARY_SEARCH_USER_DIRS);
    if(!backend){std::cout<<"LoadLibrary Win32="<<GetLastError()<<std::endl;return 2;}
    using Factory=HRESULT(WINAPI*)(HSTRING,IActivationFactory**);
    auto getFactory=reinterpret_cast<Factory>(GetProcAddress(backend,"DllGetActivationFactory"));
    if(!getFactory) return 3;
    HSTRING name=nullptr; WindowsCreateString(L"LiveCaptionsBackend.CaptionBackend",34,&name);
    IActivationFactory* factory=nullptr;
    r=getFactory(name,&factory); hr(L"DllGetActivationFactory(LiveCaptionsBackend.CaptionBackend)",r);
    if(SUCCEEDED(r) && factory){
        IInspectable* instance=nullptr;
        r=factory->ActivateInstance(&instance); hr(L"ActivateInstance",r);
        if(SUCCEEDED(r) && instance){
            HSTRING runtimeName=nullptr; r=instance->GetRuntimeClassName(&runtimeName); hr(L"GetRuntimeClassName",r);
            if(runtimeName){std::wcout<<L"RuntimeClass="<<WindowsGetStringRawBuffer(runtimeName,nullptr)<<std::endl;WindowsDeleteString(runtimeName);}
            ULONG count=0; IID* ids=nullptr; r=instance->GetIids(&count,&ids); hr(L"GetIids",r);
            if(SUCCEEDED(r)){for(ULONG i=0;i<count;i++){wchar_t id[64];StringFromGUID2(ids[i],id,64);std::wcout<<L"IID="<<id<<std::endl;}}
            if((inspect||run||file||session) && count && SUCCEEDED(r)) {
                r=instance->QueryInterface(ids[0],&ResearchBackendInterface); hr(L"QueryInterface(actual IID)",r);
                if(SUCCEEDED(r) && ResearchBackendInterface) {
                    if(inspect && IsDebuggerPresent()) DebugBreak();
                    if(run||file||session)try {r=session?runFileSession(ResearchBackendInterface,backend,argv[3]):file?runFile(ResearchBackendInterface,backend,status?nullptr:argv[3],pcm):runBackend(ResearchBackendInterface,backend,argv[3]);}
                    catch(const std::exception& ex){std::cerr<<ex.what()<<std::endl;r=E_FAIL;}
                    reinterpret_cast<IUnknown*>(ResearchBackendInterface)->Release();
                    ResearchBackendInterface=nullptr;
                }
            }
            CoTaskMemFree(ids); instance->Release();
        }
        factory->Release();
    }
    WindowsDeleteString(name); FreeLibrary(backend);
    if(c1)RemoveDllDirectory(c1);if(c2)RemoveDllDirectory(c2);if(c3)RemoveDllDirectory(c3);
    RoUninitialize(); return SUCCEEDED(r)?0:3;
}
