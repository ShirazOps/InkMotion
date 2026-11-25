using System.Runtime.CompilerServices;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.Windows.AI.MachineLearning;

namespace InkMotion;

/// <summary>
/// Runs ONNX models on this device through Windows ML. Only execution providers that are already installed are
/// registered, so starting inference never downloads anything; the CPU provider is always available as a fallback.
/// Callers must check <see cref="OnDeviceModels.UnsupportedReason()"/> first, because x86 has no Windows ML runtime.
/// </summary>
public static class WindowsMlRuntime
{
    private static readonly object Gate = new();
    private static IReadOnlyList<string>? s_providers;

    /// <summary>Names of registered hardware execution providers (empty when only the CPU is used).</summary>
    public static IReadOnlyList<string> Providers => s_providers ?? [];

    public static InferenceSession CreateSession(string modelPath)
    {
        if (OnDeviceModels.UnsupportedReason() is string reason) throw new PlatformNotSupportedException(reason);
        if (!File.Exists(modelPath)) throw new FileNotFoundException("Import the model in AI models first.", modelPath);
        Initialise();
        return Create(modelPath);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Initialise()
    {
        lock (Gate)
        {
            if (s_providers is not null) return;
            _ = OrtEnv.Instance();
            var registered = new List<string>();
            try
            {
                ExecutionProviderCatalog catalog = ExecutionProviderCatalog.GetDefault();
                foreach (ExecutionProvider provider in catalog.FindAllProviders())
                {
                    // EnsureReadyAsync could fetch a provider package; only use providers that are present already.
                    if (provider.ReadyState == ExecutionProviderReadyState.Ready && provider.TryRegister())
                        registered.Add(provider.Name);
                }
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or TypeLoadException or
                DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
            {
                // Older Windows builds lack the provider catalogue; built-in providers still work.
            }
            s_providers = registered;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static InferenceSession Create(string modelPath)
    {
        using var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        try
        {
            options.SetEpSelectionPolicy(ExecutionProviderDevicePolicy.MAX_PERFORMANCE);
            return new InferenceSession(modelPath, options);
        }
        catch (OnnxRuntimeException)
        {
            using var cpu = new SessionOptions();
            return new InferenceSession(modelPath, cpu);
        }
    }

    public static NamedOnnxValue Tensor(string name, float[] data, params int[] dimensions) =>
        NamedOnnxValue.CreateFromTensor(name, new DenseTensor<float>(data, dimensions));

    /// <summary>Checks that a model declares the named inputs and outputs InkMotion relies on.</summary>
    public static void RequireContract(InferenceSession session, string model, IEnumerable<string> inputs,
        IEnumerable<string> outputs)
    {
        string[] missing = inputs.Where(name => !session.InputMetadata.ContainsKey(name))
            .Concat(outputs.Where(name => !session.OutputMetadata.ContainsKey(name))).ToArray();
        if (missing.Length > 0)
            throw new InvalidDataException($"{model} is missing {string.Join(", ", missing)}. Re-export it with the scripts in build/models.");
    }
}
