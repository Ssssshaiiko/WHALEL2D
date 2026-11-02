// Off-screen Live2D renderer for the 大肥鱼 desktop pet (FishPet).
// Draws the model without a window and hands out premultiplied BGRA frames over a pipe,
// one 16-byte header ("L2DF", width, height, 0) before each; commands come in on stdin.
//
//   --model <model3.json>  --width W --height H --texscale 0.25 --masksize 1024 --cache <dir>
//   --png <file> [--warm s]     single frame
//   --bench <frames>            timing and memory report
//   --sweep <file.tsv>          what every parameter actually moves
//   --script <file>             run commands in simulated time
//   --pipe --fps 60             raw frames on stdout, commands on stdin
#define NOMINMAX
#include <windows.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <dxgi1_4.h>
#include <wincodec.h>
#include <wrl/client.h>
#include <psapi.h>
#include <shlwapi.h>
#include <fcntl.h>
#include <io.h>

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdio>
#include <deque>
#include <fstream>
#include <iostream>
#include <map>
#include <mutex>
#include <set>
#include <sstream>
#include <string>
#include <thread>
#include <vector>

#include <CubismFramework.hpp>
#include <CubismModelSettingJson.hpp>
#include <Utils/CubismJson.hpp>
#include <random>
#include <ICubismAllocator.hpp>
#include <Effect/CubismBreath.hpp>
#include <Effect/CubismEyeBlink.hpp>
#include <Id/CubismIdManager.hpp>
#include <Math/CubismMatrix44.hpp>
#include <Model/CubismUserModel.hpp>
#include <Motion/CubismBreathUpdater.hpp>
#include <Motion/CubismExpressionMotion.hpp>
#include <Motion/CubismEyeBlinkUpdater.hpp>
#include <Motion/CubismMotion.hpp>
#include <Motion/CubismMotionManager.hpp>
#include <Motion/CubismPhysicsUpdater.hpp>
#include <Physics/CubismPhysics.hpp>
#include <Rendering/D3D11/CubismDeviceInfo_D3D11.hpp>
#include <Rendering/D3D11/CubismRenderer_D3D11.hpp>

using Microsoft::WRL::ComPtr;
using namespace Live2D::Cubism::Framework;
using Rendering::CubismRenderer_D3D11;

static std::wstring shaderCache;

// Compiling the framework's shaders is most of a start (six to seven seconds), so their bytecode is
// kept. CMakeLists.txt points the framework's D3DCompile calls here, leaving the SDK itself untouched.
extern "C" HRESULT WINAPI CachedD3DCompile(LPCVOID source, SIZE_T size, LPCSTR name, const D3D_SHADER_MACRO* defines, ID3DInclude* include,
    LPCSTR entry, LPCSTR target, UINT flags1, UINT flags2, ID3DBlob** code, ID3DBlob** errors)
{
    unsigned long long hash = 14695981039346656037ULL;
    auto mix = [&hash](const void* data, size_t length)
    {
        for (size_t i = 0; i < length; i++) { hash ^= static_cast<const unsigned char*>(data)[i]; hash *= 1099511628211ULL; }
    };
    mix(source, size);
    if (entry) mix(entry, strlen(entry));
    mix(target, strlen(target));
    mix(&flags1, sizeof(flags1)); mix(&flags2, sizeof(flags2));
    for (auto* define = defines; define && define->Name; define++)
    {
        mix(define->Name, strlen(define->Name));
        if (define->Definition) mix(define->Definition, strlen(define->Definition));
    }
    wchar_t key[32];
    swprintf_s(key, L"\\%016llx.cso", hash);
    const std::wstring file = shaderCache.empty() ? L"" : shaderCache + key;

    FILE* handle = nullptr;
    if (!file.empty() && !_wfopen_s(&handle, file.c_str(), L"rb") && handle)
    {
        _fseeki64(handle, 0, SEEK_END);
        const long long length = _ftelli64(handle);
        _fseeki64(handle, 0, SEEK_SET);
        ID3DBlob* blob = nullptr;
        bool good = length > 0 && SUCCEEDED(D3DCreateBlob(static_cast<SIZE_T>(length), &blob))
            && fread(blob->GetBufferPointer(), 1, static_cast<size_t>(length), handle) == static_cast<size_t>(length);
        fclose(handle);
        if (good) { *code = blob; if (errors) *errors = nullptr; return S_OK; }
        if (blob) blob->Release();
    }

    const HRESULT result = D3DCompile(source, size, name, defines, include, entry, target, flags1, flags2, code, errors);
    if (SUCCEEDED(result) && !file.empty())
    {
        const std::wstring partial = file + L".tmp";
        if (!_wfopen_s(&handle, partial.c_str(), L"wb") && handle)
        {
            bool good = fwrite((*code)->GetBufferPointer(), 1, (*code)->GetBufferSize(), handle) == (*code)->GetBufferSize();
            fclose(handle);
            if (!good || !MoveFileExW(partial.c_str(), file.c_str(), MOVEFILE_REPLACE_EXISTING)) DeleteFileW(partial.c_str());
        }
    }
    return result;
}

namespace {

std::wstring exeDir;

std::wstring Wide(const std::string& text)
{
    int count = MultiByteToWideChar(CP_UTF8, 0, text.c_str(), -1, nullptr, 0);
    std::wstring out(count ? count - 1 : 0, L'\0');
    if (count > 1) MultiByteToWideChar(CP_UTF8, 0, text.c_str(), -1, out.data(), count);
    return out;
}

std::string Utf8(const std::wstring& text)
{
    int count = WideCharToMultiByte(CP_UTF8, 0, text.c_str(), -1, nullptr, 0, nullptr, nullptr);
    std::string out(count ? count - 1 : 0, '\0');
    if (count > 1) WideCharToMultiByte(CP_UTF8, 0, text.c_str(), -1, out.data(), count, nullptr, nullptr);
    return out;
}

void Fail(const std::string& message)
{
    fprintf(stderr, "错误: %s\n", message.c_str());
    exit(1);
}

void Check(HRESULT hr, const char* what)
{
    if (FAILED(hr)) { char text[160]; sprintf_s(text, "%s 失败 0x%08lx", what, static_cast<unsigned long>(hr)); Fail(text); }
}

csmByte* LoadFile(const std::string path, csmSizeInt* size)
{
    std::wstring file = Wide(path);
    if (PathIsRelativeW(file.c_str())) file = exeDir + L"\\" + file;
    FILE* handle = nullptr;
    if (_wfopen_s(&handle, file.c_str(), L"rb") || !handle) { if (size) *size = 0; return nullptr; }
    _fseeki64(handle, 0, SEEK_END);
    long long length = _ftelli64(handle);
    _fseeki64(handle, 0, SEEK_SET);
    auto* buffer = static_cast<csmByte*>(malloc(static_cast<size_t>(length) + 1));
    fread(buffer, 1, static_cast<size_t>(length), handle);
    fclose(handle);
    if (size) *size = static_cast<csmSizeInt>(length);
    return buffer;
}

void ReleaseBytes(csmByte* bytes) { free(bytes); }
void Log(const csmChar* message) { fprintf(stderr, "%s\n", message); }

class Allocator : public ICubismAllocator
{
    void* Allocate(const csmSizeType size) override { return malloc(size); }
    void Deallocate(void* memory) override { free(memory); }
    void* AllocateAligned(const csmSizeType size, const csmUint32 alignment) override { return _aligned_malloc(size, alignment); }
    void DeallocateAligned(void* memory) override { _aligned_free(memory); }
};

std::vector<std::wstring> ListFiles(const std::wstring& pattern)
{
    std::vector<std::wstring> names;
    WIN32_FIND_DATAW data;
    HANDLE find = FindFirstFileW(pattern.c_str(), &data);
    if (find == INVALID_HANDLE_VALUE) return names;
    do { if (!(data.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY)) names.push_back(data.cFileName); } while (FindNextFileW(find, &data));
    FindClose(find);
    std::sort(names.begin(), names.end());
    return names;
}

std::wstring Stem(std::wstring name, const std::wstring& suffix)
{
    if (name.size() > suffix.size()) name.resize(name.size() - suffix.size());
    return name;
}

struct Pixels { UINT width = 0, height = 0; std::vector<BYTE> data; };

// Decoding the PNGs takes seconds, so what was scaled once is kept as raw pixels. The file's size and
// time are in the name: a changed model simply misses the cache.
std::wstring CacheFile(const std::wstring& directory, const std::wstring& file, double scale)
{
    WIN32_FILE_ATTRIBUTE_DATA info;
    if (directory.empty() || !GetFileAttributesExW(file.c_str(), GetFileExInfoStandard, &info)) return L"";
    wchar_t key[96];
    swprintf_s(key, L"-%x-%x-%x-%d.bgra", info.nFileSizeLow, info.ftLastWriteTime.dwHighDateTime, info.ftLastWriteTime.dwLowDateTime, static_cast<int>(scale * 1000));
    return directory + L"\\" + file.substr(file.find_last_of(L"\\/") + 1) + key;
}

bool ReadCache(const std::wstring& file, Pixels& pixels)
{
    if (file.empty()) return false;
    FILE* handle = nullptr;
    if (_wfopen_s(&handle, file.c_str(), L"rb") || !handle) return false;
    bool good = fread(&pixels.width, sizeof(UINT), 1, handle) == 1 && fread(&pixels.height, sizeof(UINT), 1, handle) == 1;
    if (good)
    {
        pixels.data.resize(static_cast<size_t>(pixels.width) * pixels.height * 4);
        good = fread(pixels.data.data(), 1, pixels.data.size(), handle) == pixels.data.size();
    }
    fclose(handle);
    return good;
}

void WriteCache(const std::wstring& file, const Pixels& pixels)
{
    if (file.empty()) return;
    std::wstring partial = file + L".tmp";
    FILE* handle = nullptr;
    if (_wfopen_s(&handle, partial.c_str(), L"wb") || !handle) return;
    bool good = fwrite(&pixels.width, sizeof(UINT), 1, handle) == 1 && fwrite(&pixels.height, sizeof(UINT), 1, handle) == 1
        && fwrite(pixels.data.data(), 1, pixels.data.size(), handle) == pixels.data.size();
    fclose(handle);
    if (!good || !MoveFileExW(partial.c_str(), file.c_str(), MOVEFILE_REPLACE_EXISTING)) DeleteFileW(partial.c_str());
}

// Textures are premultiplied before scaling: the atlases are mostly transparent, and scaling
// straight alpha would pull the colour of empty pixels into every edge.
Pixels DecodeTexture(IWICImagingFactory* wic, const std::wstring& file, double scale)
{
    ComPtr<IWICBitmapDecoder> decoder;
    Check(wic->CreateDecoderFromFilename(file.c_str(), nullptr, GENERIC_READ, WICDecodeMetadataCacheOnDemand, &decoder), "打开贴图");
    ComPtr<IWICBitmapFrameDecode> frame;
    Check(decoder->GetFrame(0, &frame), "读取贴图帧");
    ComPtr<IWICFormatConverter> converter;
    Check(wic->CreateFormatConverter(&converter), "创建格式转换");
    Check(converter->Initialize(frame.Get(), GUID_WICPixelFormat32bppPBGRA, WICBitmapDitherTypeNone, nullptr, 0, WICBitmapPaletteTypeCustom), "转换贴图格式");
    UINT width = 0, height = 0;
    Check(converter->GetSize(&width, &height), "读取贴图尺寸");
    UINT outWidth = std::max(1u, static_cast<UINT>(width * scale)), outHeight = std::max(1u, static_cast<UINT>(height * scale));
    ComPtr<IWICBitmapSource> source = converter;
    ComPtr<IWICBitmapScaler> scaler;
    if (outWidth != width || outHeight != height)
    {
        Check(wic->CreateBitmapScaler(&scaler), "创建缩放器");
        Check(scaler->Initialize(converter.Get(), outWidth, outHeight, WICBitmapInterpolationModeFant), "缩放贴图");
        source = scaler;
    }
    Pixels pixels;
    pixels.width = outWidth; pixels.height = outHeight;
    pixels.data.resize(static_cast<size_t>(outWidth) * outHeight * 4);
    Check(source->CopyPixels(nullptr, outWidth * 4, static_cast<UINT>(pixels.data.size()), pixels.data.data()), "解码贴图");
    return pixels;
}

ComPtr<ID3D11ShaderResourceView> LoadTexture(ID3D11Device* device, ID3D11DeviceContext* context, IWICImagingFactory* wic,
    const std::wstring& file, double scale, const std::wstring& cache, size_t* bytes)
{
    const std::wstring cached = CacheFile(cache, file, scale);
    Pixels pixels;
    if (!ReadCache(cached, pixels))
    {
        pixels = DecodeTexture(wic, file, scale);
        WriteCache(cached, pixels);
    }

    D3D11_TEXTURE2D_DESC desc = {};
    desc.Width = pixels.width; desc.Height = pixels.height; desc.MipLevels = 0; desc.ArraySize = 1;
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM; desc.SampleDesc.Count = 1;   // WIC's scaler always hands back BGRA order.
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
    desc.MiscFlags = D3D11_RESOURCE_MISC_GENERATE_MIPS;
    ComPtr<ID3D11Texture2D> texture;
    Check(device->CreateTexture2D(&desc, nullptr, &texture), "创建贴图");
    context->UpdateSubresource(texture.Get(), 0, nullptr, pixels.data.data(), pixels.width * 4, 0);
    ComPtr<ID3D11ShaderResourceView> view;
    Check(device->CreateShaderResourceView(texture.Get(), nullptr, &view), "创建贴图视图");
    context->GenerateMips(view.Get());
    if (bytes) *bytes += pixels.data.size() * 4 / 3;
    return view;
}

struct Expression
{
    csmVector<CubismExpressionMotion::ExpressionParameter> parameters;
    float weight = 0, fade = 0.5f;
    bool on = false;
};

// One line of the author's VTube Studio setup: a face tracker value, the model parameter it drives, the
// ranges between them and how smoothly it follows. Head and body read the same head angle, the body
// a little slower, which is what keeps her from moving like a stiff puppet.
struct Mapping
{
    std::string input;
    CubismIdHandle output = nullptr;
    float inputLow = 0, inputHigh = 1, outputLow = 0, outputHigh = 1, smoothing = 0, value = 0;
    bool clampInput = false, clampOutput = false, started = false;
};

// Small, never repeating drift, the way a person on camera never holds perfectly still.
float Drift(float time, float a, float b, float c)
{
    return (std::sin(time * a) + 0.6f * std::sin(time * b + 1.3f) + 0.3f * std::sin(time * c + 2.1f)) / 1.9f;
}

struct Hold
{
    float value = 0, target = 0, weight = 0;
    bool on = true;
};

struct Track
{
    std::string id;
    std::vector<std::pair<float, float>> keys;   // Seconds and value; starts and ends at 0, so it never jumps.
    float time = 0;
    bool loop = false;
};

// Her eyelids as a mood or movement wants them, as a share of what blinking leaves open (up to 1.5, wide):
// after blinking, so a blink cannot open them again, and before physics, so her lashes follow.
struct Lids : ICubismUpdater
{
    explicit Lids(const float* open) : ICubismUpdater(CubismUpdateOrder_EyeBlink + 50), open(open) {}
    void OnLateUpdate(CubismModel* model, csmFloat32) override
    {
        auto* ids = CubismFramework::GetIdManager();
        CubismIdHandle eyes[2] = { ids->GetId("ParamEyeLOpen"), ids->GetId("ParamEyeROpen") };
        for (int i = 0; i < 2; i++)
            if (open[i] != 1) model->SetParameterValue(eyes[i], model->GetParameterValue(eyes[i]) * std::max(0.0f, open[i]));
    }
    const float* open;
};

class Pet : public CubismUserModel
{
public:
    ~Pet() override
    {
        for (auto& entry : motions) ACubismMotion::Delete(entry.second);
        delete gestures;
        delete setting;
    }

    void Load(const std::wstring& modelFile, int width, int height, double textureScale, int maskSize, const std::wstring& cache,
        ID3D11Device* device, ID3D11DeviceContext* context, IWICImagingFactory* wic)
    {
        std::wstring directory = modelFile.substr(0, modelFile.find_last_of(L"\\/") + 1);
        csmSizeInt size = 0;
        csmByte* buffer = LoadFile(Utf8(modelFile), &size);
        if (!buffer) Fail("读不到模型配置");
        setting = new CubismModelSettingJson(buffer, size);
        ReleaseBytes(buffer);

        buffer = LoadFile(Utf8(directory + Wide(setting->GetModelFileName())), &size);
        if (!buffer) Fail("读不到 moc3");
        mocVersion = GetMocVersionFromBuffer(buffer, size);
        LoadModel(buffer, size);
        ReleaseBytes(buffer);
        if (!_model) Fail("moc3 加载失败");

        buffer = LoadFile(Utf8(directory + Wide(setting->GetPhysicsFileName())), &size);
        if (buffer) { LoadPhysics(buffer, size); ReleaseBytes(buffer); }
        _updateScheduler.AddUpdatableList(CSM_NEW CubismPhysicsUpdater(*_physics));

        _eyeBlink = CubismEyeBlink::Create(setting);
        // About every 2.7 s rather than the framework's 3.5: slower looked sleepy (the owner, 2026-11-02).
        _eyeBlink->SetBlinkingInterval(3.2f);
        _updateScheduler.AddUpdatableList(CSM_NEW CubismEyeBlinkUpdater(blinkBlocked, *_eyeBlink));
        _updateScheduler.AddUpdatableList(CSM_NEW Lids(lidOpen));

        // Breath is a physics input here (30 settings read it), so it has to swing the full 0..1.
        _breath = CubismBreath::Create();
        csmVector<CubismBreath::BreathParameterData> breath;
        breath.PushBack(CubismBreath::BreathParameterData(CubismFramework::GetIdManager()->GetId("ParamBreath"), 0.5f, 0.5f, 3.2345f, 1.0f));
        _breath->SetParameters(breath);
        _updateScheduler.AddUpdatableList(CSM_NEW CubismBreathUpdater(*_breath));
        _updateScheduler.SortUpdatableList();

        _model->SaveParameters();

        for (const auto& name : ListFiles(directory + L"Exp3\\*.exp3.json"))
        {
            buffer = LoadFile(Utf8(directory + L"Exp3\\" + name), &size);
            if (!buffer) continue;
            CubismExpressionMotion* motion = CubismExpressionMotion::Create(buffer, size);
            ReleaseBytes(buffer);
            if (!motion) continue;
            Expression expression;
            expression.parameters = motion->GetExpressionParameters();
            expressions[Utf8(Stem(name, L".exp3.json"))] = expression;
            ACubismMotion::Delete(motion);
        }

        LoadTrackerSetup(directory + Stem(modelFile.substr(modelFile.find_last_of(L"\\/") + 1), L".model3.json") + L".vtube.json");

        gestures = CSM_NEW CubismMotionManager();
        for (const auto& name : ListFiles(directory + L"Animation\\*.motion3.json"))
        {
            buffer = LoadFile(Utf8(directory + L"Animation\\" + name), &size);
            if (!buffer) continue;
            std::string key = Utf8(Stem(name, L".motion3.json"));
            auto* motion = static_cast<CubismMotion*>(LoadMotion(buffer, size, key.c_str()));
            ReleaseBytes(buffer);
            if (!motion) continue;
            bool idle = key == "IDLE";
            motion->SetLoop(idle);
            motion->SetLoopFadeIn(false);
            if (!idle) { motion->SetFadeInTime(0.2f); motion->SetFadeOutTime(0.0f); }   // The author's hotkeys fade hand moves in 0.2 s.
            motions[key] = motion;
        }

        std::set<std::vector<csmInt32>> clips;
        const csmInt32* maskCounts = _model->GetDrawableMaskCounts();
        const csmInt32** masks = _model->GetDrawableMasks();
        for (csmInt32 i = 0; i < _model->GetDrawableCount(); i++)
            if (maskCounts[i] > 0) { std::vector<csmInt32> list(masks[i], masks[i] + maskCounts[i]); std::sort(list.begin(), list.end()); clips.insert(list); }
        clipCount = static_cast<int>(clips.size());
        maskBuffers = std::max(1, (clipCount + 31) / 32);

        CreateRenderer(width, height, maskBuffers);
        auto* renderer = GetRenderer<CubismRenderer_D3D11>();
        renderer->SetDrawableClippingMaskBufferSize(static_cast<csmFloat32>(maskSize), static_cast<csmFloat32>(maskSize));
        for (csmInt32 i = 0; i < setting->GetTextureCount(); i++)
        {
            auto view = LoadTexture(device, context, wic, directory + Wide(setting->GetTextureFileName(i)), textureScale, cache, &textureBytes);
            renderer->BindTexture(i, view.Get());
            textures.push_back(view);
        }
        renderer->IsPremultipliedAlpha(true);
    }

    // The author's VTube Studio file next to the model: how a tracked face drives her, and how long each
    // expression fades. Without it she only looks with her eyes.
    void LoadTrackerSetup(const std::wstring& file)
    {
        csmSizeInt size = 0;
        csmByte* buffer = LoadFile(Utf8(file), &size);
        if (!buffer) { fprintf(stderr, "没有 VTube Studio 配置，头和身体不会联动\n"); return; }
        Utils::CubismJson* json = Utils::CubismJson::Create(buffer, size);
        ReleaseBytes(buffer);
        if (!json) return;
        Utils::Value& settings = json->GetRoot()["ParameterSettings"];
        for (csmInt32 i = 0; i < settings.GetSize(); i++)
        {
            Utils::Value& entry = settings[i];
            Mapping mapping;
            mapping.input = entry["Input"].GetRawString();
            if (mapping.input.empty()) continue;   // Breathing: the framework already breathes.
            mapping.output = CubismFramework::GetIdManager()->GetId(entry["OutputLive2D"].GetRawString());
            mapping.inputLow = entry["InputRangeLower"].ToFloat(); mapping.inputHigh = entry["InputRangeUpper"].ToFloat();
            mapping.outputLow = entry["OutputRangeLower"].ToFloat(); mapping.outputHigh = entry["OutputRangeUpper"].ToFloat();
            mapping.smoothing = entry["Smoothing"].ToFloat();
            mapping.clampInput = entry["ClampInput"].ToBoolean(); mapping.clampOutput = entry["ClampOutput"].ToBoolean();
            mappings.push_back(mapping);
        }
        Utils::Value& hotkeys = json->GetRoot()["Hotkeys"];
        for (csmInt32 i = 0; i < hotkeys.GetSize(); i++)
        {
            std::string name = hotkeys[i]["File"].GetRawString();
            const std::string suffix = ".exp3.json";
            if (name.size() <= suffix.size() || name.compare(name.size() - suffix.size(), suffix.size(), suffix) != 0) continue;
            auto expression = expressions.find(name.substr(0, name.size() - suffix.size()));
            if (expression != expressions.end()) expression->second.fade = std::max(0.05f, hotkeys[i]["FadeSecondsAmount"].ToFloat(0.5f));
        }
        Utils::CubismJson::Delete(json);
    }

    void Tick(float dt)
    {
        _model->LoadParameters();
        auto idle = motions.find("IDLE");
        if (_motionManager->IsFinished()) { if (idle != motions.end()) _motionManager->StartMotionPriority(idle->second, false, 1); }
        else _motionManager->UpdateMotion(_model, dt);
        gestures->UpdateMotion(_model, dt);
        _model->SaveParameters();

        for (auto& entry : expressions)
        {
            Expression& expression = entry.second;
            float step = dt / expression.fade;
            expression.weight = expression.on ? std::min(1.0f, expression.weight + step) : std::max(0.0f, expression.weight - step);
            if (expression.weight <= 0) continue;
            for (csmUint32 i = 0; i < expression.parameters.GetSize(); i++)
            {
                const auto& parameter = expression.parameters[i];
                if (parameter.BlendType == CubismExpressionMotion::Additive) _model->AddParameterValue(parameter.ParameterId, parameter.Value, expression.weight);
                else if (parameter.BlendType == CubismExpressionMotion::Multiply) _model->MultiplyParameterValue(parameter.ParameterId, parameter.Value, expression.weight);
                else _model->SetParameterValue(parameter.ParameterId, parameter.Value, expression.weight);
            }
        }

        // What a face tracker would report for her: where she looks, a little drift, now and then her eyes
        // darting. Holds and tracks named like these inputs add to them, so her body follows her head the
        // way the author set it up instead of the head turning alone.
        auto* ids = CubismFramework::GetIdManager();
        float ease = 1 - std::exp(-dt * 10);
        lookX += (lookTargetX - lookX) * ease;
        lookY += (lookTargetY - lookY) * ease;
        aliveTime += dt;
        saccadeWait -= dt;
        if (saccadeWait <= 0)
        {
            std::uniform_real_distribution<float> unit(0, 1);
            saccadeWait = 1.2f + 2.8f * unit(random);
            saccadeTargetX = (unit(random) - 0.5f) * 0.24f;
            saccadeTargetY = (unit(random) - 0.5f) * 0.16f;
        }
        const float dart = 1 - std::exp(-dt * 25);
        saccadeX += (saccadeTargetX - saccadeX) * dart;
        saccadeY += (saccadeTargetY - saccadeY) * dart;
        std::map<std::string, float> inputs = {
            { "FaceAngleX", lookX * 22 + alive * 3.0f * Drift(aliveTime, 0.37f, 0.91f, 1.73f) },
            { "FaceAngleY", lookY * 16 + alive * 2.0f * Drift(aliveTime, 0.29f, 0.77f, 1.41f) },
            { "FaceAngleZ", alive * 3.5f * Drift(aliveTime, 0.23f, 0.63f, 1.19f) },
            { "EyeRightX", -0.6f * (lookX + alive * saccadeX) },
            { "EyeRightY", 0.7f * (lookY + alive * saccadeY) },
        };

        // How far open her eyes are: EyeOpen for both, or one eye by its parameter. Several multiply.
        lidOpen[0] = lidOpen[1] = 1;
        auto lid = [&](const std::string& id, float open) {
            const bool both = id == "EyeOpen", left = id == "ParamEyeLOpen", right = id == "ParamEyeROpen";
            if (both || left) lidOpen[0] *= std::max(0.0f, open);
            if (both || right) lidOpen[1] *= std::max(0.0f, open);
            return both || left || right;
        };

        // Held values ease in and out like her expressions, or a mood would snap on and off.
        std::vector<std::pair<std::string, Hold*>> heldParameters;
        for (auto hold = holds.begin(); hold != holds.end();)
        {
            Hold& entry = hold->second;
            entry.weight += ((entry.on ? 1.0f : 0.0f) - entry.weight) * (1 - std::exp(-dt / 0.15f));
            entry.value += (entry.target - entry.value) * (1 - std::exp(-dt / 0.15f));
            if (!entry.on && entry.weight < 0.01f) { hold = holds.erase(hold); continue; }
            auto input = inputs.find(hold->first);
            if (input != inputs.end()) input->second += entry.value * entry.weight;
            else if (!lid(hold->first, 1 + (entry.value - 1) * entry.weight)) heldParameters.push_back({ hold->first, &entry });
            ++hold;
        }

        // Her movements add to wherever she is looking, and to each other: a new one never cuts one short.
        // Tracks named View* move her whole figure: the model's own parameters cannot lift her off the ground.
        bodyX = bodyY = bodyRoll = bodySquash = 0;
        std::vector<std::pair<std::string, float>> movedParameters;
        for (auto track = tracks.begin(); track != tracks.end();)
        {
            track->time += dt;
            const float end = track->keys.back().first;
            if (track->time >= end)
            {
                if (!track->loop) { track = tracks.erase(track); continue; }
                track->time = std::fmod(track->time, end);
            }
            size_t next = 1;
            while (next + 1 < track->keys.size() && track->keys[next].first < track->time) next++;
            const auto& from = track->keys[next - 1];
            const auto& to = track->keys[next];
            float part = std::clamp((track->time - from.first) / std::max(0.0001f, to.first - from.first), 0.0f, 1.0f);
            part = part * part * (3 - 2 * part);
            const float value = from.second + (to.second - from.second) * part;
            auto input = inputs.find(track->id);
            if (input != inputs.end()) input->second += value;
            else if (track->id == "ViewX") bodyX += value;
            else if (track->id == "ViewY") bodyY += value;
            else if (track->id == "ViewRoll") bodyRoll += value;
            else if (track->id == "ViewSquash") bodySquash += value;
            else if (!lid(track->id, 1 + value)) movedParameters.push_back({ track->id, value });
            ++track;
        }

        // The author's mapping: ranges and smoothing exactly as in his VTube Studio file.
        for (auto& mapping : mappings)
        {
            auto input = inputs.find(mapping.input);
            if (input == inputs.end()) continue;
            const float span = mapping.inputHigh - mapping.inputLow;
            float part = span == 0 ? 0 : (input->second - mapping.inputLow) / span;
            if (mapping.clampInput) part = std::clamp(part, 0.0f, 1.0f);
            float target = mapping.outputLow + part * (mapping.outputHigh - mapping.outputLow);
            if (mapping.clampOutput) target = std::clamp(target, std::min(mapping.outputLow, mapping.outputHigh), std::max(mapping.outputLow, mapping.outputHigh));
            const float lag = mapping.smoothing * 0.005f;
            mapping.value = !mapping.started || lag <= 0 ? target : mapping.value + (target - mapping.value) * (1 - std::exp(-dt / lag));
            mapping.started = true;
            _model->SetParameterValue(mapping.output, mapping.value);
        }
        for (const auto& [id, hold] : heldParameters) _model->SetParameterValue(ids->GetId(id.c_str()), hold->value, hold->weight);
        for (const auto& [id, value] : movedParameters) _model->AddParameterValue(ids->GetId(id.c_str()), value);

        // Moving the window pushes the body, and physics carries that into hair, skirt and tail.
        float swayEase = 1 - std::exp(-dt * 8), swayDecay = std::exp(-dt * 6);
        swayX += (swayTargetX - swayX) * swayEase; swayY += (swayTargetY - swayY) * swayEase;
        swayTargetX *= swayDecay; swayTargetY *= swayDecay;
        _model->AddParameterValue(ids->GetId("PositionX"), swayX);
        _model->AddParameterValue(ids->GetId("PositionY"), swayY);
        _model->AddParameterValue(ids->GetId("AngleZ"), -swayX * 0.4f);

        // Her mouth opens fast and closes a little slower, and a mood's open mouth is never shut by it.
        mouth += (mouthTarget - mouth) * (1 - std::exp(-dt * (mouthTarget > mouth ? 30 : 16)));
        if (mouth > 0.01f)
        {
            auto* openId = ids->GetId("ParamMouthOpenY");
            auto* jawId = ids->GetId("JawOpen");
            _model->SetParameterValue(openId, std::max(_model->GetParameterValue(openId), mouth));
            _model->SetParameterValue(jawId, std::max(_model->GetParameterValue(jawId), mouth * 0.6f));
        }

        _updateScheduler.OnLateUpdate(_model, dt);
        _model->Update();
    }

    void Draw(ID3D11DeviceContext* context, int width, int height, float zoom, float centerX, float centerY)
    {
        auto* renderer = GetRenderer<CubismRenderer_D3D11>();
        renderer->StartFrame(context);
        float sx = zoom * height / width, sy = zoom;
        csmFloat32 array[16] = { sx, 0, 0, 0, 0, sy, 0, 0, 0, 0, 1, 0, -centerX * sx, -centerY * sy, 0, 1 };
        CubismMatrix44 matrix;
        matrix.SetMatrix(array);
        // Turning and squashing happen around her feet, and a bigger body travels further.
        const float radians = bodyRoll * 3.14159265f / 180, cosine = std::cos(radians), sine = std::sin(radians);
        const float wide = 1 + bodySquash * 0.6f, tall = 1 - bodySquash;
        const float a = cosine * wide, b = -sine * tall, c = sine * wide, d = cosine * tall;
        csmFloat32 body[16] = { a, c, 0, 0, b, d, 0, 0, 0, 0, 1, 0,
            -b * anchorY + bodyX * anchorScale, anchorY - d * anchorY + bodyY * anchorScale, 0, 1 };
        CubismMatrix44 bodyMatrix;
        bodyMatrix.SetMatrix(body);
        matrix.MultiplyByMatrix(&bodyMatrix);
        matrix.MultiplyByMatrix(_modelMatrix);
        renderer->SetMvpMatrix(&matrix);
        renderer->DrawModel();
        renderer->EndFrame();
    }

    bool SetExpression(const std::string& name, bool on)
    {
        auto entry = expressions.find(name);
        if (entry == expressions.end()) return false;
        entry->second.on = on;
        return true;
    }
    void ClearExpressions() { for (auto& entry : expressions) entry.second.on = false; }

    bool PlayMotion(const std::string& name)
    {
        auto entry = motions.find(name);
        if (entry == motions.end()) return false;
        if (name == "IDLE") _motionManager->StartMotionPriority(entry->second, false, 1);
        else gestures->StartMotionPriority(entry->second, false, 3);
        return true;
    }

    // Measures on Core alone: only the parameter's own effect, without physics.
    void Sweep(const std::wstring& file)
    {
        const csmInt32 parameters = _model->GetParameterCount();
        const csmInt32 drawables = _model->GetDrawableCount();
        const float unit = _model->GetCanvasHeight();
        for (csmInt32 i = 0; i < parameters; i++) _model->SetParameterValue(i, _model->GetParameterDefaultValue(i));
        _model->Update();
        std::vector<std::vector<float>> basePositions(drawables);
        std::vector<float> baseOpacity(drawables);
        for (csmInt32 d = 0; d < drawables; d++)
        {
            const csmInt32 count = _model->GetDrawableVertexCount(d) * 2;
            const csmFloat32* vertices = _model->GetDrawableVertices(d);
            basePositions[d].assign(vertices, vertices + count);
            baseOpacity[d] = _model->GetDrawableOpacity(d);
        }
        std::ofstream out(file, std::ios::binary);
        out << "id\tmin\tdefault\tmax\tmoved\tmaxMove\tfaded\tmaxFade\tparts\n";
        for (csmInt32 p = 0; p < parameters; p++)
        {
            const float low = _model->GetParameterMinimumValue(p), high = _model->GetParameterMaximumValue(p), start = _model->GetParameterDefaultValue(p);
            int moved = 0, faded = 0;
            float maxMove = 0, maxFade = 0;
            std::map<std::string, int> parts;
            std::vector<char> movedAt(drawables, 0), fadedAt(drawables, 0);
            for (float value : { low, high })
            {
                if (value == start) continue;
                _model->SetParameterValue(p, value);
                _model->Update();
                for (csmInt32 d = 0; d < drawables; d++)
                {
                    const csmFloat32* vertices = _model->GetDrawableVertices(d);
                    float move = 0;
                    for (size_t v = 0; v < basePositions[d].size(); v++) move = std::max(move, std::fabs(vertices[v] - basePositions[d][v]));
                    float fade = std::fabs(_model->GetDrawableOpacity(d) - baseOpacity[d]);
                    if (move > 1e-5f) movedAt[d] = 1;
                    if (fade > 1e-3f) fadedAt[d] = 1;
                    maxMove = std::max(maxMove, move);
                    maxFade = std::max(maxFade, fade);
                }
            }
            for (csmInt32 d = 0; d < drawables; d++)
            {
                moved += movedAt[d]; faded += fadedAt[d];
                if (movedAt[d] || fadedAt[d])
                {
                    csmInt32 part = _model->GetDrawableParentPartIndex(d);
                    if (part >= 0) parts[_model->GetPartId(part)->GetString().GetRawString()]++;
                }
            }
            _model->SetParameterValue(p, start);
            std::vector<std::pair<int, std::string>> ranked;
            for (const auto& entry : parts) ranked.push_back({ entry.second, entry.first });
            std::sort(ranked.rbegin(), ranked.rend());
            out << _model->GetParameterId(p)->GetString().GetRawString() << '\t' << low << '\t' << start << '\t' << high << '\t'
                << moved << '\t' << maxMove / unit << '\t' << faded << '\t' << maxFade << '\t';
            for (size_t r = 0; r < std::min<size_t>(ranked.size(), 6); r++) out << (r ? "," : "") << ranked[r].second;
            out << '\n';
        }
        _model->Update();
    }

    std::map<std::string, Hold> holds;
    std::map<std::string, Expression> expressions;
    std::vector<Track> tracks;
    std::vector<Mapping> mappings;
    std::mt19937 random{ 20261102 };
    float alive = 1, aliveTime = 0, saccadeWait = 2, saccadeX = 0, saccadeY = 0, saccadeTargetX = 0, saccadeTargetY = 0;
    float mouthTarget = 0, mouth = 0;
    float lidOpen[2] = { 1, 1 };
    float bodyX = 0, bodyY = 0, bodyRoll = 0, bodySquash = 0, anchorY = -0.92f, anchorScale = 1;
    std::map<std::string, ACubismMotion*> motions;
    float lookTargetX = 0, lookTargetY = 0, lookX = 0, lookY = 0;
    float swayTargetX = 0, swayTargetY = 0, swayX = 0, swayY = 0;
    size_t textureBytes = 0;
    int clipCount = 0, maskBuffers = 1;
    int mocVersion = 0;

private:
    CubismModelSettingJson* setting = nullptr;
    CubismMotionManager* gestures = nullptr;
    csmBool blinkBlocked = false;   // IDLE never touches the eyes, so it must not stop blinking.
    std::vector<ComPtr<ID3D11ShaderResourceView>> textures;
};

struct Stage
{
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<IDXGIAdapter3> adapter;
    ComPtr<ID3D11Texture2D> color, depth, staging;
    ComPtr<ID3D11RenderTargetView> target;
    ComPtr<ID3D11DepthStencilView> depthView;
    ComPtr<ID3D11DepthStencilState> noDepth;
    std::wstring adapterName;
    int width = 0, height = 0;

    void Create(int w, int h)
    {
        ComPtr<IDXGIFactory1> factory;
        Check(CreateDXGIFactory1(IID_PPV_ARGS(&factory)), "创建 DXGI");
        ComPtr<IDXGIAdapter1> best;
        SIZE_T bestMemory = 0;
        for (UINT i = 0;; i++)
        {
            ComPtr<IDXGIAdapter1> candidate;
            if (factory->EnumAdapters1(i, &candidate) == DXGI_ERROR_NOT_FOUND) break;
            DXGI_ADAPTER_DESC1 desc;
            candidate->GetDesc1(&desc);
            if (desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) continue;
            if (desc.DedicatedVideoMemory > bestMemory) { bestMemory = desc.DedicatedVideoMemory; best = candidate; adapterName = desc.Description; }
        }
        if (!best) Fail("没有可用显卡");
        best.As(&adapter);
        Check(D3D11CreateDevice(best.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, D3D11_CREATE_DEVICE_SINGLETHREADED, nullptr, 0, D3D11_SDK_VERSION, &device, nullptr, &context), "创建 D3D11 设备");
        D3D11_DEPTH_STENCIL_DESC state = {};
        state.DepthEnable = false; state.DepthWriteMask = D3D11_DEPTH_WRITE_MASK_ALL; state.DepthFunc = D3D11_COMPARISON_LESS;
        Check(device->CreateDepthStencilState(&state, &noDepth), "创建深度状态");
        Resize(w, h);
    }

    void Resize(int w, int h)
    {
        width = w; height = h;
        context->OMSetRenderTargets(0, nullptr, nullptr);
        target.Reset(); depthView.Reset(); color.Reset(); staging.Reset(); depth.Reset();
        context->Flush();
        D3D11_TEXTURE2D_DESC desc = {};
        desc.Width = w; desc.Height = h; desc.MipLevels = 1; desc.ArraySize = 1;
        desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM; desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE_DEFAULT; desc.BindFlags = D3D11_BIND_RENDER_TARGET;
        Check(device->CreateTexture2D(&desc, nullptr, &color), "创建渲染目标");
        Check(device->CreateRenderTargetView(color.Get(), nullptr, &target), "创建渲染视图");
        desc.Usage = D3D11_USAGE_STAGING; desc.BindFlags = 0; desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        Check(device->CreateTexture2D(&desc, nullptr, &staging), "创建回读贴图");
        desc.Format = DXGI_FORMAT_D24_UNORM_S8_UINT; desc.Usage = D3D11_USAGE_DEFAULT;
        desc.BindFlags = D3D11_BIND_DEPTH_STENCIL; desc.CPUAccessFlags = 0;
        Check(device->CreateTexture2D(&desc, nullptr, &depth), "创建深度贴图");
        Check(device->CreateDepthStencilView(depth.Get(), nullptr, &depthView), "创建深度视图");
    }

    void Begin()
    {
        const float clear[4] = { 0, 0, 0, 0 };
        ID3D11RenderTargetView* view = target.Get();
        context->OMSetRenderTargets(1, &view, depthView.Get());
        context->ClearRenderTargetView(target.Get(), clear);
        context->ClearDepthStencilView(depthView.Get(), D3D11_CLEAR_DEPTH | D3D11_CLEAR_STENCIL, 1.0f, 0);
        context->OMSetDepthStencilState(noDepth.Get(), 0);
    }

    void Read(std::vector<BYTE>& frame)
    {
        context->CopyResource(staging.Get(), color.Get());
        D3D11_MAPPED_SUBRESOURCE mapped;
        Check(context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped), "回读帧");
        const auto* source = static_cast<const BYTE*>(mapped.pData);
        for (int y = 0; y < height; y++) memcpy(frame.data() + static_cast<size_t>(y) * width * 4, source + static_cast<size_t>(y) * mapped.RowPitch, static_cast<size_t>(width) * 4);
        context->Unmap(staging.Get(), 0);
    }

    double VideoMemoryMb()
    {
        DXGI_QUERY_VIDEO_MEMORY_INFO info = {};
        if (FAILED(adapter->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &info))) return -1;
        return info.CurrentUsage / 1048576.0;
    }
};

void SavePng(IWICImagingFactory* wic, const std::wstring& file, const std::vector<BYTE>& frame, int width, int height, const int background[3], bool flatten)
{
    std::vector<BYTE> pixels(frame.size());
    for (size_t i = 0; i < frame.size(); i += 4)
    {
        if (!flatten) { memcpy(&pixels[i], &frame[i], 4); continue; }
        int rest = 255 - frame[i + 3];
        for (int c = 0; c < 3; c++) pixels[i + c] = static_cast<BYTE>(std::min(255, frame[i + c] + background[2 - c] * rest / 255));
        pixels[i + 3] = 255;
    }
    ComPtr<IWICStream> stream;
    Check(wic->CreateStream(&stream), "创建图片流");
    Check(stream->InitializeFromFilename(file.c_str(), GENERIC_WRITE), "打开图片文件");
    ComPtr<IWICBitmapEncoder> encoder;
    Check(wic->CreateEncoder(GUID_ContainerFormatPng, nullptr, &encoder), "创建 PNG 编码器");
    Check(encoder->Initialize(stream.Get(), WICBitmapEncoderNoCache), "初始化编码器");
    ComPtr<IWICBitmapFrameEncode> target;
    Check(encoder->CreateNewFrame(&target, nullptr), "创建图片帧");
    Check(target->Initialize(nullptr), "初始化图片帧");
    Check(target->SetSize(width, height), "设置图片大小");
    WICPixelFormatGUID format = flatten ? GUID_WICPixelFormat32bppBGRA : GUID_WICPixelFormat32bppPBGRA;
    Check(target->SetPixelFormat(&format), "设置像素格式");
    Check(target->WritePixels(height, width * 4, static_cast<UINT>(pixels.size()), pixels.data()), "写入图片");
    Check(target->Commit(), "提交图片帧");
    Check(encoder->Commit(), "提交图片");
}

struct View { float zoom = 1, centerX = 0, centerY = 0; int background[3] = { 236, 240, 246 }; bool flatten = true; };

void Exec(const std::string& line, Pet& pet, View& view)
{
    std::istringstream input(line);
    std::string command, a, b, c;
    input >> command >> a >> b >> c;
    if (command == "exp") { if (!pet.SetExpression(a, b != "0")) fprintf(stderr, "没有这个表情: %s\n", a.c_str()); }
    else if (command == "motion") { if (!pet.PlayMotion(a)) fprintf(stderr, "没有这个动作: %s\n", a.c_str()); }
    // A held value that is already showing moves over to the new one instead of jumping there.
    else if (command == "set")
    {
        const bool shown = pet.holds.count(a) && pet.holds[a].weight >= 0.01f;
        Hold& hold = pet.holds[a];
        hold.target = static_cast<float>(atof(b.c_str()));
        if (!shown) hold.value = hold.target;
        hold.on = true;
    }
    else if (command == "clear") { auto hold = pet.holds.find(a); if (hold != pet.holds.end()) hold->second.on = false; }
    else if (command == "clearall") { for (auto& hold : pet.holds) hold.second.on = false; pet.ClearExpressions(); }
    else if (command == "mouth") pet.mouthTarget = std::clamp(static_cast<float>(atof(a.c_str())), 0.0f, 1.0f);
    else if (command == "anim" || command == "animloop")   // anim <parameter> <seconds>:<value> ...
    {
        Track track;
        track.id = a;
        track.loop = command == "animloop";
        std::istringstream keys(line.substr(line.find(a) + a.size()));
        std::string key;
        while (keys >> key)
        {
            const size_t split = key.find(':');
            if (split != std::string::npos) track.keys.push_back({ static_cast<float>(atof(key.c_str())), static_cast<float>(atof(key.c_str() + split + 1)) });
        }
        if (track.keys.size() >= 2) pet.tracks.push_back(track); else fprintf(stderr, "动作轨迹不对: %s\n", line.c_str());
    }
    else if (command == "animstop") { for (auto& track : pet.tracks) track.loop = false; }   // Loops finish their round.
    else if (command == "alive") pet.alive = std::clamp(static_cast<float>(atof(a.c_str())), 0.0f, 1.0f);
    else if (command == "anchor") { pet.anchorY = static_cast<float>(atof(a.c_str())); pet.anchorScale = static_cast<float>(atof(b.c_str())); }
    // Runs time forward without drawing, so a body change is over before anyone sees it happen.
    else if (command == "settle") { for (int i = 0; i < 90; i++) pet.Tick(1.0f / 60); }
    else if (command == "look") { pet.lookTargetX = static_cast<float>(atof(a.c_str())); pet.lookTargetY = static_cast<float>(atof(b.c_str())); }
    else if (command == "sway")
    {
        pet.swayTargetX = std::clamp(static_cast<float>(atof(a.c_str())) / 40, -30.0f, 30.0f);
        pet.swayTargetY = std::clamp(static_cast<float>(atof(b.c_str())) / 40, -30.0f, 30.0f);
    }
    else if (command == "view") { view.zoom = static_cast<float>(atof(a.c_str())); view.centerX = static_cast<float>(atof(b.c_str())); view.centerY = static_cast<float>(atof(c.c_str())); }
    else if (command == "bg") { view.background[0] = atoi(a.c_str()); view.background[1] = atoi(b.c_str()); view.background[2] = atoi(c.c_str()); }
    else if (command == "alpha") view.flatten = a == "0";
    else if (!command.empty()) fprintf(stderr, "不认识的命令: %s\n", line.c_str());
}

double Ms(std::chrono::steady_clock::time_point from, std::chrono::steady_clock::time_point to)
{
    return std::chrono::duration<double, std::milli>(to - from).count();
}

}  // namespace

int wmain(int argc, wchar_t** argv)
{
    wchar_t self[MAX_PATH];
    GetModuleFileNameW(nullptr, self, MAX_PATH);
    exeDir = self; exeDir.resize(exeDir.find_last_of(L"\\/"));

    std::map<std::wstring, std::wstring> options;
    for (int i = 1; i < argc; i++)
    {
        std::wstring key = argv[i];
        if (key.rfind(L"--", 0) != 0) continue;
        bool hasValue = i + 1 < argc && std::wstring(argv[i + 1]).rfind(L"--", 0) != 0;
        options[key.substr(2)] = hasValue ? argv[++i] : L"1";
    }
    auto text = [&](const wchar_t* key, const wchar_t* fallback) { auto found = options.find(key); return found == options.end() ? std::wstring(fallback) : found->second; };
    auto number = [&](const wchar_t* key, double fallback) { auto found = options.find(key); return found == options.end() ? fallback : _wtof(found->second.c_str()); };
    if (!options.count(L"model")) Fail("要用 --model 指定 model3.json");

    int width = static_cast<int>(number(L"width", 420)), height = static_cast<int>(number(L"height", 860));
    View view;
    view.zoom = static_cast<float>(number(L"zoom", 1));
    view.centerX = static_cast<float>(number(L"cx", 0));
    view.centerY = static_cast<float>(number(L"cy", 0));

    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    auto loadStart = std::chrono::steady_clock::now();
    Stage stage;
    stage.Create(width, height);
    ComPtr<IWICImagingFactory> wic;
    Check(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&wic)), "创建 WIC");

    static Allocator allocator;
    static CubismFramework::Option option;
    option.LogFunction = Log;
    option.LoggingLevel = CubismFramework::Option::LogLevel_Warning;
    option.LoadFileFunction = LoadFile;
    option.ReleaseBytesFunction = ReleaseBytes;
    CubismFramework::StartUp(&allocator, &option);
    CubismFramework::Initialize();
    CubismRenderer_D3D11::SetConstantSettings(1, stage.device.Get());

    double beforeModel = stage.VideoMemoryMb();
    auto* pet = new Pet();
    const std::wstring cache = text(L"cache", L"");
    if (!cache.empty()) CreateDirectoryW(cache.c_str(), nullptr);
    shaderCache = cache;
    pet->Load(text(L"model", L""), width, height, number(L"texscale", 0.25), static_cast<int>(number(L"masksize", 1024)), cache,
        stage.device.Get(), stage.context.Get(), wic.Get());
    double loadSeconds = Ms(loadStart, std::chrono::steady_clock::now()) / 1000;

    fprintf(stderr, "显卡: %s\n", Utf8(stage.adapterName).c_str());
    fprintf(stderr, "加载 %.2f 秒，moc 版本 %d，遮罩 %d 组用 %d 张，贴图约 %.0f MB，表情 %zu，动作 %zu\n", loadSeconds, pet->mocVersion, pet->clipCount, pet->maskBuffers,
        pet->textureBytes / 1048576.0, pet->expressions.size(), pet->motions.size());

    std::vector<BYTE> frame(static_cast<size_t>(width) * height * 4);
    auto render = [&]() { stage.Begin(); pet->Draw(stage.context.Get(), width, height, view.zoom, view.centerX, view.centerY); };

    if (options.count(L"sweep"))
    {
        pet->Sweep(text(L"sweep", L""));
    }
    else if (options.count(L"png"))
    {
        for (int i = 0, n = static_cast<int>(number(L"warm", 2) * 60); i < n; i++) pet->Tick(1.0f / 60);
        render(); stage.Read(frame);
        SavePng(wic.Get(), text(L"png", L""), frame, width, height, view.background, view.flatten);
    }
    else if (options.count(L"script"))
    {
        std::ifstream script(text(L"script", L""), std::ios::binary);
        std::string line;
        while (std::getline(script, line))
        {
            if (!line.empty() && line.back() == '\r') line.pop_back();
            if (line.rfind("\xEF\xBB\xBF", 0) == 0) line.erase(0, 3);
            if (line.empty() || line[0] == '#') continue;
            std::istringstream input(line);
            std::string command, argument;
            input >> command >> argument;
            if (command == "step") { for (int i = 0, n = static_cast<int>(atof(argument.c_str()) * 60); i < n; i++) pet->Tick(1.0f / 60); }
            else if (command == "png") { render(); stage.Read(frame); SavePng(wic.Get(), Wide(argument), frame, width, height, view.background, view.flatten); }
            else Exec(line, *pet, view);
        }
    }
    else if (options.count(L"bench"))
    {
        const int frames = static_cast<int>(number(L"bench", 600));
        const float dt = 1.0f / static_cast<float>(number(L"fps", 30));
        for (int i = 0; i < 90; i++) pet->Tick(dt);
        std::vector<double> tick, draw, read;
        for (int i = 0; i < frames; i++)
        {
            auto t0 = std::chrono::steady_clock::now();
            pet->lookTargetX = std::sin(i * 0.05f) * 0.6f;
            pet->Tick(dt);
            auto t1 = std::chrono::steady_clock::now();
            render();
            auto t2 = std::chrono::steady_clock::now();
            stage.Read(frame);
            auto t3 = std::chrono::steady_clock::now();
            tick.push_back(Ms(t0, t1)); draw.push_back(Ms(t1, t2)); read.push_back(Ms(t2, t3));
        }
        auto report = [](const char* name, std::vector<double> values)
        {
            std::sort(values.begin(), values.end());
            double sum = 0; for (double value : values) sum += value;
            fprintf(stderr, "%-6s 平均 %.2f ms  p95 %.2f ms  最大 %.2f ms\n", name, sum / values.size(), values[values.size() * 95 / 100], values.back());
        };
        report("更新", tick); report("绘制", draw); report("回读", read);
        PROCESS_MEMORY_COUNTERS counters = {};
        GetProcessMemoryInfo(GetCurrentProcess(), &counters, sizeof(counters));
        fprintf(stderr, "内存 %.0f MB，模型增加显存约 %.0f MB\n", counters.WorkingSetSize / 1048576.0, stage.VideoMemoryMb() - beforeModel);
    }
    else if (options.count(L"pipe"))
    {
        _setmode(_fileno(stdout), _O_BINARY);
        std::mutex lock;
        std::deque<std::string> commands;
        bool finished = false;
        std::thread reader([&]()
        {
            std::string line;
            while (std::getline(std::cin, line))
            {
                if (!line.empty() && line.back() == '\r') line.pop_back();
                std::lock_guard<std::mutex> guard(lock);
                commands.push_back(line);
            }
            std::lock_guard<std::mutex> guard(lock);
            finished = true;   // The pet went away: never outlive it.
        });
        reader.detach();
        timeBeginPeriod(1);
        double frameMs = 1000.0 / number(L"fps", 60);
        bool paused = false;
        auto next = std::chrono::steady_clock::now();
        auto last = next;
        for (;;)
        {
            {
                std::lock_guard<std::mutex> guard(lock);
                if (finished) break;
                bool quit = false;
                while (!commands.empty())
                {
                    const std::string& command = commands.front();
                    int w = 0, h = 0, value = 0;
                    if (command == "quit") quit = true;
                    else if (sscanf_s(command.c_str(), "fps %d", &value) == 1 && value > 0) frameMs = 1000.0 / value;
                    else if (sscanf_s(command.c_str(), "pause %d", &value) == 1) paused = value != 0;
                    else if (sscanf_s(command.c_str(), "resize %d %d", &w, &h) == 2 && w > 0 && h > 0)
                    {
                        width = w; height = h;
                        stage.Resize(w, h);
                        pet->SetRenderTargetSize(w, h);
                        frame.resize(static_cast<size_t>(w) * h * 4);
                    }
                    else Exec(command, *pet, view);
                    commands.pop_front();
                }
                if (quit) break;
            }
            if (paused)   // Hidden or behind a fullscreen game: no drawing at all until she is back.
            {
                std::this_thread::sleep_for(std::chrono::milliseconds(50));
                next = last = std::chrono::steady_clock::now();
                continue;
            }
            auto now = std::chrono::steady_clock::now();
            float dt = std::min(0.1f, static_cast<float>(Ms(last, now) / 1000));
            last = now;
            pet->Tick(dt);
            render();
            stage.Read(frame);
            const int header[4] = { 0x4644324C, width, height, 0 };   // "L2DF": the size can change between frames.
            if (fwrite(header, sizeof(header), 1, stdout) != 1 || fwrite(frame.data(), 1, frame.size(), stdout) != frame.size() || fflush(stdout) != 0) break;
            next += std::chrono::microseconds(static_cast<long long>(frameMs * 1000));
            auto wait = next - std::chrono::steady_clock::now();
            if (wait.count() > 0) std::this_thread::sleep_for(wait); else next = std::chrono::steady_clock::now();
        }
        timeEndPeriod(1);
        _exit(0);
    }

    pet->DeleteRenderer();
    delete pet;
    Rendering::CubismDeviceInfo_D3D11::ReleaseAllDeviceInfo();
    CubismFramework::Dispose();
    return 0;
}
