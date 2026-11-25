#include "public.sdk/source/vst/hosting/hostclasses.h"
#include "public.sdk/source/vst/hosting/module.h"
#include "public.sdk/source/vst/hosting/parameterchanges.h"
#include "public.sdk/source/vst/hosting/plugprovider.h"
#include "public.sdk/source/vst/hosting/processdata.h"
#include "public.sdk/source/vst/utility/stringconvert.h"
#include "pluginterfaces/vst/ivstaudioprocessor.h"
#include "pluginterfaces/vst/ivsteditcontroller.h"
#include "pluginterfaces/vst/ivstprocesscontext.h"
#include "pluginterfaces/vst/vstspeaker.h"

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <iostream>
#include <memory>
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
using Steinberg::Vst::IEditController;
using Steinberg::Vst::IAudioProcessor;
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

    std::cerr << "Usage: InkMotionVst3Host --version | --scan <module-path> | "
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
