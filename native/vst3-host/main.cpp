#include "public.sdk/source/vst/hosting/hostclasses.h"
#include "public.sdk/source/vst/hosting/module.h"
#include "public.sdk/source/vst/hosting/parameterchanges.h"
#include "public.sdk/source/vst/hosting/plugprovider.h"
#include "public.sdk/source/vst/hosting/processdata.h"
#include "public.sdk/source/vst/utility/stringconvert.h"
#include "pluginterfaces/gui/iplugview.h"
#include "pluginterfaces/vst/ivstaudioprocessor.h"
#include "pluginterfaces/vst/ivsteditcontroller.h"
#include "pluginterfaces/vst/ivstprocesscontext.h"
#include "pluginterfaces/vst/vstspeaker.h"

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <iostream>
#include <iomanip>
#include <limits>
#include <memory>
#include <sstream>
#include <stdexcept>
#include <string>
#include <type_traits>
#include <utility>
#include <vector>

#if defined(_WIN32)
#include <fcntl.h>
#include <io.h>
#include <windows.h>
#endif

namespace {

using Steinberg::Vst::AudioBusBuffers;
using Steinberg::Vst::BusInfo;
using Steinberg::Vst::HostApplication;
using Steinberg::Vst::HostProcessData;
using Steinberg::Vst::IComponent;
using Steinberg::Vst::IComponentHandler;
using Steinberg::Vst::IEditController;
using Steinberg::Vst::IAudioProcessor;
using Steinberg::IPlugView;
using Steinberg::IPlugFrame;
using Steinberg::Vst::ParameterChanges;
using Steinberg::Vst::ParameterInfo;
using Steinberg::Vst::ProcessContext;
using Steinberg::Vst::ProcessSetup;
using Steinberg::Vst::SpeakerArrangement;

enum class Command : uint8_t
{
    Process = 1,
    GetParameters = 2,
    SetParameter = 3,
    Shutdown = 4,
    GetTailSamples = 5,
};

std::string JsonString(const std::string& value)
{
    std::string result = "\"";
    for (unsigned char character : value)
    {
        switch (character)
        {
        case '"':
            result += "\\\"";
            break;
        case '\\':
            result += "\\\\";
            break;
        case '\b':
            result += "\\b";
            break;
        case '\f':
            result += "\\f";
            break;
        case '\n':
            result += "\\n";
            break;
        case '\r':
            result += "\\r";
            break;
        case '\t':
            result += "\\t";
            break;
        default:
            if (character < 0x20)
            {
                constexpr char digits[] = "0123456789abcdef";
                result += "\\u00";
                result += digits[character >> 4];
                result += digits[character & 0x0f];
            }
            else
            {
                result += static_cast<char>(character);
            }
        }
    }

    result += '"';
    return result;
}

#if defined(_WIN32)
std::string ToUtf8(const wchar_t* value)
{
    int byteCount = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value, -1, nullptr, 0, nullptr, nullptr);
    if (byteCount <= 0)
    {
        return {};
    }

    std::vector<char> bytes(static_cast<size_t>(byteCount));
    if (WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value, -1, bytes.data(), byteCount, nullptr, nullptr) <= 0)
    {
        return {};
    }

    return std::string(bytes.data());
}

void SetBinaryStdio()
{
    _setmode(_fileno(stdin), _O_BINARY);
    _setmode(_fileno(stdout), _O_BINARY);
}
#endif

bool ReadBytes(void* value, size_t count)
{
    std::cin.read(static_cast<char*>(value), static_cast<std::streamsize>(count));
    return std::cin.gcount() == static_cast<std::streamsize>(count);
}

bool WriteBytes(const void* value, size_t count)
{
    std::cout.write(static_cast<const char*>(value), static_cast<std::streamsize>(count));
    std::cout.flush();
    return static_cast<bool>(std::cout);
}

template <typename T>
bool ReadValue(T& value)
{
    static_assert(std::is_trivially_copyable_v<T>);
    return ReadBytes(&value, sizeof(value));
}

template <typename T>
bool WriteValue(const T& value)
{
    static_assert(std::is_trivially_copyable_v<T>);
    return WriteBytes(&value, sizeof(value));
}

void WriteError(const std::string& message)
{
    const uint8_t status = 1;
    const uint32_t length = static_cast<uint32_t>(message.size());
    WriteValue(status);
    WriteValue(length);
    WriteBytes(message.data(), length);
}

int ScanModule(const std::string& path)
{
    std::string error;
    auto module = VST3::Hosting::Module::create(path, error);
    if (!module)
    {
        std::cerr << error << '\n';
        return 2;
    }

    std::vector<VST3::Hosting::ClassInfo> effects;
    for (const auto& classInfo : module->getFactory().classInfos())
    {
        if (classInfo.category() == kVstAudioEffectClass)
        {
            effects.push_back(classInfo);
        }
    }

    std::cout << "{\"path\":" << JsonString(module->getPath()) << ",\"effects\":[";
    for (size_t index = 0; index < effects.size(); ++index)
    {
        if (index != 0)
        {
            std::cout << ',';
        }

        const auto& effect = effects[index];
        std::cout << "{\"classId\":" << JsonString(effect.ID().toString())
                  << ",\"name\":" << JsonString(effect.name())
                  << ",\"vendor\":" << JsonString(effect.vendor())
                  << ",\"version\":" << JsonString(effect.version()) << '}';
    }
    std::cout << "]}\n";
    return 0;
}

class PluginWorker
{
public:
    PluginWorker(const std::string& path, const std::string& classId, double sampleRate,
        int32_t channels, int32_t maxBlock)
        : sampleRate_(sampleRate), channels_(channels), maxBlock_(maxBlock)
    {
        if (!std::isfinite(sampleRate_) || sampleRate_ < 8000 || sampleRate_ > 384000 ||
            (channels_ != 1 && channels_ != 2) || maxBlock_ < 1 || maxBlock_ > 8192)
        {
            throw std::invalid_argument("Unsupported sample rate, channel count or block size.");
        }

        hostApplication_ = Steinberg::owned(new HostApplication());
        Steinberg::Vst::PluginContextFactory::instance().setPluginContext(hostApplication_);

        std::string error;
        module_ = VST3::Hosting::Module::create(path, error);
        if (!module_)
        {
            throw std::runtime_error(error.empty() ? "Could not load the VST3 module." : error);
        }

        const auto& factory = module_->getFactory();
        auto classInfos = factory.classInfos();
        auto selected = std::find_if(classInfos.begin(), classInfos.end(), [&](const auto& info)
        {
            return info.category() == kVstAudioEffectClass && info.ID().toString() == classId;
        });
        if (selected == classInfos.end())
        {
            throw std::runtime_error("The requested audio effect class was not found in the VST3 module.");
        }

        provider_ = std::make_unique<Steinberg::Vst::PlugProvider>(factory, *selected);
        if (!provider_->initialize())
        {
            throw std::runtime_error("The VST3 component could not be initialised.");
        }

        component_ = provider_->getComponentPtr();
        controller_ = provider_->getControllerPtr();
        processor_ = component_;
        if (!processor_)
        {
            throw std::runtime_error("The VST3 component does not expose an audio processor.");
        }

        ConfigureBus(Steinberg::Vst::kInput);
        ConfigureBus(Steinberg::Vst::kOutput);

        SpeakerArrangement arrangement = channels_ == 1
            ? Steinberg::Vst::SpeakerArr::kMono
            : Steinberg::Vst::SpeakerArr::kStereo;
        if (processor_->setBusArrangements(&arrangement, 1, &arrangement, 1) != Steinberg::kResultOk)
        {
            throw std::runtime_error("The VST3 effect does not accept this mono/stereo channel layout.");
        }

        processContext_ = {};
        processContext_.sampleRate = sampleRate_;
        if (!processData_.prepare(*component_, maxBlock_, Steinberg::Vst::kSample32))
        {
            throw std::runtime_error("The VST3 effect's audio buffers could not be prepared.");
        }
        processData_.processMode = Steinberg::Vst::kRealtime;
        processData_.symbolicSampleSize = Steinberg::Vst::kSample32;
        processData_.processContext = &processContext_;
        processData_.inputParameterChanges = &parameterChanges_;
        parameterChanges_.setMaxParameters(1024);

        ProcessSetup setup {
            Steinberg::Vst::kRealtime,
            Steinberg::Vst::kSample32,
            maxBlock_,
            sampleRate_,
        };
        if (processor_->setupProcessing(setup) != Steinberg::kResultOk ||
            component_->setActive(true) != Steinberg::kResultOk ||
            processor_->setProcessing(true) != Steinberg::kResultOk)
        {
            throw std::runtime_error("The VST3 effect could not enter its processing state.");
        }
    }

    ~PluginWorker()
    {
        if (processor_)
        {
            processor_->setProcessing(false);
        }
        if (component_)
        {
            component_->setActive(false);
        }
        processor_ = nullptr;
        controller_ = nullptr;
        component_ = nullptr;
        provider_.reset();
        Steinberg::Vst::PluginContextFactory::instance().setPluginContext(nullptr);
    }

    bool Process()
    {
        uint32_t frames = 0;
        if (!ReadValue(frames))
        {
            return false;
        }
        if (frames == 0 || frames > static_cast<uint32_t>(maxBlock_))
        {
            WriteError("The audio block size is outside the configured limit.");
            return true;
        }

        std::vector<float> interleaved(static_cast<size_t>(frames) * channels_);
        if (!ReadBytes(interleaved.data(), interleaved.size() * sizeof(float)))
        {
            return false;
        }

        AudioBusBuffers& inputBus = processData_.inputs[0];
        AudioBusBuffers& outputBus = processData_.outputs[0];
        for (uint32_t frame = 0; frame < frames; ++frame)
        {
            for (int32_t channel = 0; channel < channels_; ++channel)
            {
                inputBus.channelBuffers32[channel][frame] =
                    interleaved[static_cast<size_t>(frame) * channels_ + channel];
            }
        }

        processData_.numSamples = static_cast<Steinberg::int32>(frames);
        processData_.inputParameterChanges = &parameterChanges_;

        for (const auto& [id, value] : pendingParameters_)
        {
            int32_t queueIndex = 0;
            auto* queue = parameterChanges_.addParameterData(id, queueIndex);
            if (queue)
            {
                queue->addPoint(0, value, queueIndex);
            }
        }
        pendingParameters_.clear();

        if (processor_->process(processData_) != Steinberg::kResultOk)
        {
            parameterChanges_.clearQueue();
            WriteError("The VST3 effect failed while processing audio.");
            return true;
        }
        parameterChanges_.clearQueue();

        for (uint32_t frame = 0; frame < frames; ++frame)
        {
            for (int32_t channel = 0; channel < channels_; ++channel)
            {
                interleaved[static_cast<size_t>(frame) * channels_ + channel] =
                    outputBus.channelBuffers32[channel][frame];
            }
        }

        const uint8_t status = 0;
        return WriteValue(status) && WriteBytes(interleaved.data(), interleaved.size() * sizeof(float));
    }

    bool WriteParameters()
    {
        const uint8_t status = 0;
        if (!WriteValue(status))
        {
            return false;
        }

        uint32_t count = controller_ ? controller_->getParameterCount() : 0;
        if (count > 10000)
        {
            WriteError("The VST3 effect exposes too many parameters.");
            return true;
        }
        if (!WriteValue(count))
        {
            return false;
        }

        for (uint32_t index = 0; index < count; ++index)
        {
            ParameterInfo info {};
            if (controller_->getParameterInfo(index, info) != Steinberg::kResultOk)
            {
                return false;
            }

            const uint32_t id = info.id;
            const int32_t steps = info.stepCount;
            const uint32_t flags = info.flags;
            const double value = controller_->getParamNormalized(info.id);
            const std::string title = Steinberg::Vst::StringConvert::convert(info.title);
            const std::string units = Steinberg::Vst::StringConvert::convert(info.units);
            if (!WriteValue(id) || !WriteValue(steps) || !WriteValue(flags) || !WriteValue(value) ||
                !WriteString(title) || !WriteString(units))
            {
                return false;
            }
        }
        return true;
    }

    bool SetParameter()
    {
        uint32_t id = 0;
        double value = 0;
        if (!ReadValue(id) || !ReadValue(value))
        {
            return false;
        }

        if (!controller_ || !std::isfinite(value) || value < 0 || value > 1)
        {
            WriteError("The VST3 parameter value is invalid or no controller is available.");
            return true;
        }

        controller_->setParamNormalized(id, value);
        pendingParameters_.emplace_back(id, value);
        const uint8_t status = 0;
        return WriteValue(status);
    }

    bool WriteTailSamples()
    {
        const uint8_t status = 0;
        const uint32_t tailSamples = processor_->getTailSamples();
        return WriteValue(status) && WriteValue(tailSamples);
    }

private:
    bool ConfigureBus(Steinberg::Vst::BusDirection direction)
    {
        if (component_->getBusCount(Steinberg::Vst::kAudio, direction) < 1)
        {
            throw std::runtime_error("The VST3 effect has no main audio input or output bus.");
        }

        BusInfo info {};
        if (component_->getBusInfo(Steinberg::Vst::kAudio, direction, 0, info) != Steinberg::kResultOk ||
            info.channelCount != channels_ ||
            component_->activateBus(Steinberg::Vst::kAudio, direction, 0, true) != Steinberg::kResultOk)
        {
            throw std::runtime_error("The VST3 effect does not provide the requested mono/stereo bus.");
        }
        return true;
    }

    bool WriteString(const std::string& value)
    {
        const uint32_t length = static_cast<uint32_t>(value.size());
        return WriteValue(length) && WriteBytes(value.data(), length);
    }

    double sampleRate_;
    int32_t channels_;
    int32_t maxBlock_;
    Steinberg::IPtr<HostApplication> hostApplication_;
    VST3::Hosting::Module::Ptr module_;
    std::unique_ptr<Steinberg::Vst::PlugProvider> provider_;
    Steinberg::IPtr<IComponent> component_;
    Steinberg::IPtr<IEditController> controller_;
    Steinberg::FUnknownPtr<IAudioProcessor> processor_;
    HostProcessData processData_;
    ProcessContext processContext_ {};
    ParameterChanges parameterChanges_;
    std::vector<std::pair<uint32_t, double>> pendingParameters_;
};

int RunWorker(const std::string& path, const std::string& classId, double sampleRate,
    int32_t channels, int32_t maxBlock)
{
    try
    {
        PluginWorker worker(path, classId, sampleRate, channels, maxBlock);
        const uint8_t ready = 0;
        if (!WriteValue(ready))
        {
            return 3;
        }

        uint8_t rawCommand = 0;
        while (ReadValue(rawCommand))
        {
            switch (static_cast<Command>(rawCommand))
            {
            case Command::Process:
                if (!worker.Process())
                {
                    return 0;
                }
                break;
            case Command::GetParameters:
                if (!worker.WriteParameters())
                {
                    return 3;
                }
                break;
            case Command::SetParameter:
                if (!worker.SetParameter())
                {
                    return 3;
                }
                break;
            case Command::Shutdown:
            {
                const uint8_t status = 0;
                WriteValue(status);
                return 0;
            }
            case Command::GetTailSamples:
                if (!worker.WriteTailSamples())
                {
                    return 3;
                }
                break;
            default:
                WriteError("Unknown VST3 host command.");
                break;
            }
        }
        return 0;
    }
    catch (const std::exception& exception)
    {
        WriteError(exception.what());
        std::cerr << exception.what() << '\n';
        return 2;
    }
}

#if defined(_WIN32)
constexpr UINT kApplyEditorParametersMessage = WM_APP + 1;
constexpr UINT_PTR kEditorInputTimer = 1;
constexpr UINT kEditorInputIntervalMilliseconds = 40;
constexpr wchar_t kEditorWindowClass[] = L"InkMotionVst3Editor";

class EditorComponentHandler final : public IComponentHandler
{
public:
    Steinberg::tresult PLUGIN_API beginEdit(Steinberg::Vst::ParamID) override
    {
        return Steinberg::kResultOk;
    }

    Steinberg::tresult PLUGIN_API performEdit(
        Steinberg::Vst::ParamID id, Steinberg::Vst::ParamValue value) override
    {
        std::cout << "P " << id << ' ' << std::setprecision(17) << value << '\n' << std::flush;
        return Steinberg::kResultOk;
    }

    Steinberg::tresult PLUGIN_API endEdit(Steinberg::Vst::ParamID) override
    {
        return Steinberg::kResultOk;
    }

    Steinberg::tresult PLUGIN_API restartComponent(Steinberg::int32) override
    {
        return Steinberg::kResultOk;
    }

private:
    Steinberg::tresult PLUGIN_API queryInterface(const Steinberg::TUID iid, void** object) override
    {
        if (!object)
        {
            return Steinberg::kInvalidArgument;
        }
        *object = nullptr;
        if (Steinberg::FUnknownPrivate::iidEqual(iid, IComponentHandler::iid) ||
            Steinberg::FUnknownPrivate::iidEqual(iid, Steinberg::FUnknown::iid))
        {
            *object = this;
            addRef();
            return Steinberg::kResultOk;
        }
        return Steinberg::kNoInterface;
    }

    Steinberg::uint32 PLUGIN_API addRef() override { return 1000; }
    Steinberg::uint32 PLUGIN_API release() override { return 1000; }
};

class EditorWindow final : public IPlugFrame
{
public:
    EditorWindow(IPlugView* view, IEditController* controller)
        : view_(view), controller_(controller)
    {
    }

    bool Open()
    {
        if (!view_ || !controller_ ||
            view_->isPlatformTypeSupported(Steinberg::kPlatformTypeHWND) != Steinberg::kResultOk)
        {
            throw std::runtime_error("The VST3 effect does not provide a Windows native editor.");
        }
        resizable_ = view_->canResize() == Steinberg::kResultOk;
        windowStyle_ = WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX | WS_CLIPCHILDREN | WS_CLIPSIBLINGS;
        if (resizable_)
        {
            windowStyle_ |= WS_SIZEBOX | WS_MAXIMIZEBOX;
        }

        WNDCLASSEXW windowClass {};
        windowClass.cbSize = sizeof(windowClass);
        windowClass.lpfnWndProc = WindowProcedure;
        windowClass.hInstance = GetModuleHandleW(nullptr);
        windowClass.hCursor = LoadCursorW(nullptr, IDC_ARROW);
        windowClass.hbrBackground = reinterpret_cast<HBRUSH>(COLOR_WINDOW + 1);
        windowClass.lpszClassName = kEditorWindowClass;
        RegisterClassExW(&windowClass);

        Steinberg::ViewRect size {};
        if (view_->getSize(&size) != Steinberg::kResultOk ||
            size.right <= size.left || size.bottom <= size.top)
        {
            throw std::runtime_error("The VST3 editor returned an invalid initial size.");
        }

        RECT bounds {0, 0, size.right - size.left, size.bottom - size.top};
        AdjustWindowRect(&bounds, windowStyle_, FALSE);
        window_ = CreateWindowExW(
            0, kEditorWindowClass, L"InkMotion VST3 Editor", windowStyle_,
            CW_USEDEFAULT, CW_USEDEFAULT, bounds.right - bounds.left, bounds.bottom - bounds.top,
            nullptr, nullptr, windowClass.hInstance, this);
        if (!window_)
        {
            throw std::runtime_error("The VST3 editor window could not be created.");
        }

        if (view_->setFrame(this) != Steinberg::kResultOk ||
            view_->attached(window_, Steinberg::kPlatformTypeHWND) != Steinberg::kResultOk)
        {
            view_->removed();
            DestroyWindow(window_);
            window_ = nullptr;
            throw std::runtime_error("The VST3 editor could not attach to its Windows host window.");
        }

        attached_ = true;
        RECT clientBounds {};
        GetClientRect(window_, &clientBounds);
        Steinberg::ViewRect clientSize {0, 0, clientBounds.right, clientBounds.bottom};
        view_->onSize(&clientSize);
        SetTimer(window_, kEditorInputTimer, kEditorInputIntervalMilliseconds, nullptr);
        ShowWindow(window_, SW_SHOW);
        UpdateWindow(window_);
        std::cout << "S READY\n" << std::flush;

        MSG message {};
        int messageResult = 0;
        while ((messageResult = GetMessageW(&message, nullptr, 0, 0)) > 0)
        {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        if (messageResult == -1)
        {
            throw std::runtime_error("The VST3 editor window message loop failed.");
        }
        return true;
    }

    Steinberg::tresult PLUGIN_API resizeView(IPlugView* view, Steinberg::ViewRect* newSize) override
    {
        if (view != view_ || !window_ || !newSize || resizing_ ||
            newSize->right <= newSize->left || newSize->bottom <= newSize->top)
        {
            return Steinberg::kInvalidArgument;
        }

        RECT bounds {0, 0, newSize->right - newSize->left, newSize->bottom - newSize->top};
        AdjustWindowRect(&bounds, windowStyle_, FALSE);
        resizing_ = true;
        SetWindowPos(window_, nullptr, 0, 0, bounds.right - bounds.left, bounds.bottom - bounds.top,
            SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
        Steinberg::tresult result = view_->onSize(newSize);
        resizing_ = false;
        return result;
    }

    Steinberg::tresult PLUGIN_API queryInterface(const Steinberg::TUID iid, void** object) override
    {
        if (!object)
        {
            return Steinberg::kInvalidArgument;
        }
        *object = nullptr;
        if (Steinberg::FUnknownPrivate::iidEqual(iid, IPlugFrame::iid) ||
            Steinberg::FUnknownPrivate::iidEqual(iid, Steinberg::FUnknown::iid))
        {
            *object = this;
            addRef();
            return Steinberg::kResultOk;
        }
        return Steinberg::kNoInterface;
    }

    Steinberg::uint32 PLUGIN_API addRef() override { return 1000; }
    Steinberg::uint32 PLUGIN_API release() override { return 1000; }

private:
    static LRESULT CALLBACK WindowProcedure(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
    {
        auto* self = reinterpret_cast<EditorWindow*>(GetWindowLongPtrW(window, GWLP_USERDATA));
        if (message == WM_NCCREATE)
        {
            auto* create = reinterpret_cast<CREATESTRUCTW*>(lParam);
            self = static_cast<EditorWindow*>(create->lpCreateParams);
            self->window_ = window;
            SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(self));
        }
        return self ? self->HandleMessage(message, wParam, lParam)
                    : DefWindowProcW(window, message, wParam, lParam);
    }

    LRESULT HandleMessage(UINT message, WPARAM wParam, LPARAM lParam)
    {
        switch (message)
        {
        case WM_SIZE:
            if (view_ && attached_ && !resizing_ && wParam != SIZE_MINIMIZED)
            {
                RECT bounds {};
                GetClientRect(window_, &bounds);
                Steinberg::ViewRect size {0, 0, bounds.right, bounds.bottom};
                view_->onSize(&size);
            }
            return 0;
        case WM_SIZING:
            if (view_ && attached_ && resizable_)
            {
                auto* requestedBounds = reinterpret_cast<RECT*>(lParam);
                RECT currentBounds {};
                RECT currentClient {};
                GetWindowRect(window_, &currentBounds);
                GetClientRect(window_, &currentClient);
                int frameWidth = currentBounds.right - currentBounds.left -
                    (currentClient.right - currentClient.left);
                int frameHeight = currentBounds.bottom - currentBounds.top -
                    (currentClient.bottom - currentClient.top);
                Steinberg::ViewRect requestedSize {
                    0, 0,
                    requestedBounds->right - requestedBounds->left - frameWidth,
                    requestedBounds->bottom - requestedBounds->top - frameHeight,
                };
                if (view_->checkSizeConstraint(&requestedSize) == Steinberg::kResultOk)
                {
                    int width = requestedSize.right - requestedSize.left + frameWidth;
                    int height = requestedSize.bottom - requestedSize.top + frameHeight;
                    if (wParam == WMSZ_LEFT || wParam == WMSZ_TOPLEFT || wParam == WMSZ_BOTTOMLEFT)
                    {
                        requestedBounds->left = requestedBounds->right - width;
                    }
                    else
                    {
                        requestedBounds->right = requestedBounds->left + width;
                    }
                    if (wParam == WMSZ_TOP || wParam == WMSZ_TOPLEFT || wParam == WMSZ_TOPRIGHT)
                    {
                        requestedBounds->top = requestedBounds->bottom - height;
                    }
                    else
                    {
                        requestedBounds->bottom = requestedBounds->top + height;
                    }
                }
            }
            return TRUE;
        case WM_SETFOCUS:
            if (view_)
            {
                view_->onFocus(true);
            }
            return 0;
        case WM_KILLFOCUS:
            if (view_)
            {
                view_->onFocus(false);
            }
            return 0;
        case WM_TIMER:
            if (wParam == kEditorInputTimer)
            {
                ReadEditorCommands();
            }
            return 0;
        case kApplyEditorParametersMessage:
            ApplyEditorCommands();
            return 0;
        case WM_CLOSE:
            KillTimer(window_, kEditorInputTimer);
            if (view_)
            {
                view_->removed();
            }
            attached_ = false;
            DestroyWindow(window_);
            return 0;
        case WM_DESTROY:
            window_ = nullptr;
            PostQuitMessage(0);
            return 0;
        default:
            return DefWindowProcW(window_, message, wParam, lParam);
        }
    }

    void ReadEditorCommands()
    {
        HANDLE input = GetStdHandle(STD_INPUT_HANDLE);
        DWORD available = 0;
        if (input == INVALID_HANDLE_VALUE || !PeekNamedPipe(input, nullptr, 0, nullptr, &available, nullptr) ||
            available == 0)
        {
            return;
        }

        char bytes[512];
        DWORD read = 0;
        if (!ReadFile(input, bytes, std::min<DWORD>(available, sizeof(bytes)), &read, nullptr) || read == 0)
        {
            return;
        }

        inputBuffer_.append(bytes, read);
        size_t newline = 0;
        while ((newline = inputBuffer_.find('\n')) != std::string::npos)
        {
            std::string command = inputBuffer_.substr(0, newline);
            if (!command.empty() && command.back() == '\r')
            {
                command.pop_back();
            }
            pendingCommands_.push_back(std::move(command));
            inputBuffer_.erase(0, newline + 1);
        }
        if (!pendingCommands_.empty())
        {
            PostMessageW(window_, kApplyEditorParametersMessage, 0, 0);
        }
    }

    void ApplyEditorCommands()
    {
        for (const std::string& command : pendingCommands_)
        {
            size_t separator = command.find('=');
            if (separator == std::string::npos)
            {
                std::cout << "E Invalid parameter update received from InkMotion.\n" << std::flush;
                continue;
            }
            try
            {
                size_t idLength = 0;
                size_t valueLength = 0;
                unsigned long id = std::stoul(command.substr(0, separator), &idLength);
                double value = std::stod(command.substr(separator + 1), &valueLength);
                if (idLength == separator && valueLength == command.size() - separator - 1 &&
                    id <= std::numeric_limits<Steinberg::Vst::ParamID>::max() &&
                    std::isfinite(value) && value >= 0 && value <= 1)
                {
                    controller_->setParamNormalized(static_cast<Steinberg::Vst::ParamID>(id), value);
                }
            }
            catch (const std::exception&)
            {
                std::cout << "E Invalid parameter update received from InkMotion.\n" << std::flush;
            }
        }
        pendingCommands_.clear();
    }

    IPlugView* view_;
    IEditController* controller_;
    HWND window_ {};
    DWORD windowStyle_ {};
    bool resizable_ {};
    bool attached_ {};
    bool resizing_ {};
    std::string inputBuffer_;
    std::vector<std::string> pendingCommands_;
};

void ApplyInitialEditorParameters(IEditController* controller, const std::string& values)
{
    std::istringstream entries(values);
    std::string entry;
    while (std::getline(entries, entry, ';'))
    {
        size_t separator = entry.find('=');
        if (separator == std::string::npos)
        {
            continue;
        }
        size_t idLength = 0;
        size_t valueLength = 0;
        unsigned long id = std::stoul(entry.substr(0, separator), &idLength);
        double value = std::stod(entry.substr(separator + 1), &valueLength);
        if (idLength != separator || valueLength != entry.size() - separator - 1 ||
            id > std::numeric_limits<Steinberg::Vst::ParamID>::max() ||
            !std::isfinite(value) || value < 0 || value > 1)
        {
            throw std::invalid_argument("The VST3 editor received an invalid parameter value.");
        }
        controller->setParamNormalized(static_cast<Steinberg::Vst::ParamID>(id), value);
    }
}

int RunEditor(const std::string& path, const std::string& classId)
{
    try
    {
        std::string initialParameters;
        if (!std::getline(std::cin, initialParameters))
        {
            throw std::runtime_error("The VST3 editor did not receive its initial parameters.");
        }
        if (!initialParameters.empty() && initialParameters.back() == '\r')
        {
            initialParameters.pop_back();
        }

        auto hostApplication = Steinberg::owned(new HostApplication());
        Steinberg::Vst::PluginContextFactory::instance().setPluginContext(hostApplication);
        std::string error;
        auto module = VST3::Hosting::Module::create(path, error);
        if (!module)
        {
            throw std::runtime_error(error.empty() ? "Could not load the VST3 module." : error);
        }

        const auto& factory = module->getFactory();
        auto classInfos = factory.classInfos();
        auto selected = std::find_if(classInfos.begin(), classInfos.end(), [&](const auto& info)
        {
            return info.category() == kVstAudioEffectClass && info.ID().toString() == classId;
        });
        if (selected == classInfos.end())
        {
            throw std::runtime_error("The requested audio effect class was not found in the VST3 module.");
        }

        auto provider = Steinberg::owned(new Steinberg::Vst::PlugProvider(factory, *selected));
        if (!provider->initialize())
        {
            throw std::runtime_error("The VST3 component could not be initialised.");
        }

        IEditController* controller = provider->getControllerPtr();
        if (!controller)
        {
            throw std::runtime_error("The VST3 effect does not expose an edit controller.");
        }
        EditorComponentHandler handler;
        controller->setComponentHandler(&handler);
        ApplyInitialEditorParameters(controller, initialParameters);

        Steinberg::IPtr<IPlugView> view(controller->createView(Steinberg::Vst::ViewType::kEditor));
        if (!view)
        {
            throw std::runtime_error("The VST3 effect does not provide a native editor.");
        }

        EditorWindow window(view, controller);
        window.Open();
        controller->setComponentHandler(nullptr);
        Steinberg::Vst::PluginContextFactory::instance().setPluginContext(nullptr);
        return 0;
    }
    catch (const std::exception& exception)
    {
        std::string message = exception.what();
        std::replace(message.begin(), message.end(), '\n', ' ');
        std::cout << "E " << message << '\n' << std::flush;
        Steinberg::Vst::PluginContextFactory::instance().setPluginContext(nullptr);
        return 2;
    }
}
#endif

int Run(int argumentCount, const std::vector<std::string>& arguments)
{
    if (argumentCount == 2 && arguments[1] == "--version")
    {
#if defined(_M_IX86)
        constexpr auto architecture = "x86";
#elif defined(_M_X64)
        constexpr auto architecture = "x64";
#elif defined(_M_ARM64)
        constexpr auto architecture = "arm64";
#else
        constexpr auto architecture = "unknown";
#endif
        std::cout << "{\"protocol\":1,\"architecture\":\"" << architecture << "\"}\n";
        return 0;
    }

    if (argumentCount == 3 && arguments[1] == "--scan")
    {
        return ScanModule(arguments[2]);
    }

#if defined(_WIN32)
    if (argumentCount == 4 && arguments[1] == "--editor")
    {
        return RunEditor(arguments[2], arguments[3]);
    }
#endif

    if (argumentCount == 7 && arguments[1] == "--worker")
    {
        try
        {
            return RunWorker(arguments[2], arguments[3], std::stod(arguments[4]),
                std::stoi(arguments[5]), std::stoi(arguments[6]));
        }
        catch (const std::exception& exception)
        {
            std::cerr << exception.what() << '\n';
            return 2;
        }
    }

    std::cerr << "Usage: InkMotionVst3Host --version | --scan <module-path> | --editor <module-path> <class-id> | "
        "--worker <module-path> <class-id> <sample-rate> <channels> <max-block>\n";
    return 1;
}

} // namespace

#if defined(_WIN32)
int wmain(int argc, wchar_t* argv[])
{
    std::vector<std::string> arguments;
    arguments.reserve(static_cast<size_t>(argc));
    for (int index = 0; index < argc; ++index)
    {
        std::string value = ToUtf8(argv[index]);
        if (value.empty())
        {
            std::cerr << "Could not convert a command-line argument to UTF-8.\n";
            return 2;
        }
        arguments.push_back(std::move(value));
    }

    if (argc > 1 && arguments[1] == "--worker")
    {
        SetBinaryStdio();
    }
    return Run(argc, arguments);
}
#else
int main(int argc, char* argv[])
{
    std::vector<std::string> arguments(argv, argv + argc);
    return Run(argc, arguments);
}
#endif
